using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Data;

/// <summary>
/// Owns the explicit boundary between SQLite in the browser file system and the
/// versioned payload in localStorage. Corrupt or incompatible payloads are
/// preserved for export until the user explicitly resets them; a payload one
/// schema behind is upgraded rather than refused (see <see cref="SchemaUpgrades"/>).
/// </summary>
public sealed class BrowserDatabase
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>
    /// The largest payload this will try to store. <c>localStorage</c> is about
    /// 5 MB per origin in every mainstream browser and Base64 inflates by 4/3, so
    /// refusing just below the ceiling turns "the tab threw" into a message the
    /// visitor can act on while there is still room to export.
    /// </summary>
    public const int MaxPayloadCharacters = 4_500_000;

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IBrowserDatabaseStorage _storage;
    private readonly BrowserDatabaseOptions _options;
    private readonly ILogger<BrowserDatabase> _logger;
    private readonly IPermissionGuard _guard;

    // Blazor WebAssembly is single-threaded, but async continuations interleave
    // freely: two saves that overlap between "read the file" and "write the
    // payload" can store a torn snapshot that fails quick_check on the next load.
    private readonly SemaphoreSlim _persistGate = new(1, 1);

    private Task<BrowserDatabaseReadiness>? _ready;

    // The revision this tab's copy is based on. Every write carries it, and
    // storage refuses the write if another tab has moved on since - which is the
    // whole of the fix: the page that loses the race is told, instead of silently
    // writing its own stale image over work that was already saved.
    //
    // -1 means "write whatever I have": replacing the entire database (import,
    // reset, the first write of a fresh one) is not an edit that can be stale.
    private long _revision = Unconditional;

    private const long Unconditional = -1;

    public BrowserDatabase(
        IDbContextFactory<AppDbContext> factory,
        IBrowserDatabaseStorage storage,
        BrowserDatabaseOptions options,
        ILogger<BrowserDatabase> logger,
        IPermissionGuard? guard = null)
    {
        _factory = factory;
        _storage = storage;
        _options = options;
        _logger = logger;
        _guard = guard ?? AllowAllGuard.Instance;
    }

    /// <summary>
    /// Runs start-up once and hands every later caller the same result.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no <see cref="CancellationToken"/>. Start-up is shared:
    /// whoever happens to ask first would otherwise decide, with their own token,
    /// whether everybody else's database finishes loading. Each caller cancels
    /// its own await instead, and the operations that follow - contexts,
    /// snapshots, upgrades - all take tokens of their own.
    /// </remarks>
    public Task<BrowserDatabaseReadiness> EnsureReadyAsync() => _ready ??= StartAsync();

    private async Task<BrowserDatabaseReadiness> StartAsync()
    {
        try
        {
            return await InitializeAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // A failed start must not be memoised as the permanent answer.
            _ready = null;
            throw;
        }
    }

    public async Task<AppDbContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var readiness = await EnsureReadyAsync();
        if (!readiness.IsReady)
            throw new BrowserDatabaseUnavailableException(readiness);

        return await _factory.CreateDbContextAsync(cancellationToken);
    }

    public async Task<BrowserStorageResult> PersistAsync(CancellationToken cancellationToken = default)
    {
        await _persistGate.WaitAsync(cancellationToken);
        try
        {
            string payload;
            try
            {
                await CheckpointAsync(cancellationToken);
                payload = Convert.ToBase64String(await File.ReadAllBytesAsync(_options.DatabasePath, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Reading the browser database for a snapshot failed");
                return new(false, BrowserDatabaseFailure.WriteFailed);
            }

            if (payload.Length > MaxPayloadCharacters)
            {
                _logger.LogError(
                    "The browser database payload is {Characters} characters, over the {Limit} this browser can store",
                    payload.Length,
                    MaxPayloadCharacters);
                return new(false, BrowserDatabaseFailure.QuotaExceeded);
            }

            try
            {
                var written = await _storage.SaveAsync(
                    new StoredDatabase(payload, _options.SchemaVersion), _revision, cancellationToken);
                if (written.IsSuccess)
                {
                    _revision = written.Revision;
                    return BrowserStorageResult.Success;
                }

                _logger.LogError("Storing the browser database failed: {Detail}", written.Detail);
                return new(
                    false,
                    written.Outcome switch
                    {
                        StorageWriteOutcome.QuotaExceeded => BrowserDatabaseFailure.QuotaExceeded,
                        StorageWriteOutcome.ChangedElsewhere => BrowserDatabaseFailure.ChangedElsewhere,
                        _ => BrowserDatabaseFailure.WriteFailed
                    });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Persisting the browser database failed");
                return new(false, BrowserDatabaseFailure.WriteFailed);
            }
        }
        finally
        {
            _persistGate.Release();
        }
    }

    /// <summary>
    /// The database file as it stands, so a mutation whose snapshot fails can be
    /// undone. <c>SaveChanges</c> and the durable snapshot are two operations, and
    /// without this the failed half stays live for the rest of the session and is
    /// gone after a reload — the page saying "save failed" over a row that every
    /// other page still shows.
    /// </summary>
    internal async Task<byte[]?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CheckpointAsync(cancellationToken);
            return File.Exists(_options.DatabasePath)
                ? await File.ReadAllBytesAsync(_options.DatabasePath, cancellationToken)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not capture the database before a mutation");
            return null;
        }
    }

    /// <summary>Puts a captured image back, so the session stops showing what storage does not hold.</summary>
    internal async Task<bool> RestoreAsync(byte[]? image, CancellationToken cancellationToken = default)
    {
        if (image is null)
            return false;

        try
        {
            SqliteConnection.ClearAllPools();
            await File.WriteAllBytesAsync(_options.DatabasePath, image, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Rolling the database back to its pre-mutation state failed");
            return false;
        }
    }

    /// <summary>
    /// Writes the stored payload to a file the visitor downloads.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> behind a policy, unlike reset and import. The data
    /// is already in this visitor's own browser and every persona can read all of
    /// it on screen; a policy here would restrict nothing and would suggest a
    /// confidentiality boundary the app does not have.
    /// </remarks>
    public async Task<BrowserStorageResult> ExportStoredPayloadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stored = await _storage.LoadAsync(cancellationToken);
            if (stored is null)
                return new(false, BrowserDatabaseFailure.ExportFailed);

            await _storage.ExportAsync(stored, cancellationToken);
            return BrowserStorageResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Exporting the browser database payload failed");
            return new(false, BrowserDatabaseFailure.ExportFailed);
        }
    }

    /// <summary>
    /// Installs a payload the visitor exported earlier, which is what makes the
    /// export honest: until now the file it produced could be read by nothing.
    /// An older payload is upgraded on the way in, exactly as a stored one is.
    /// </summary>
    public async Task<BrowserDatabaseReadiness> ImportAsync(CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ResetData, cancellationToken))
            return new(false, BrowserDatabaseFailure.Forbidden);

        StoredDatabase? picked;
        try
        {
            picked = await _storage.PickImportAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Reading the imported payload failed");
            return new(false, BrowserDatabaseFailure.ReadFailed);
        }

        if (picked is null)
            return await EnsureReadyAsync();   // cancelled: nothing changes

        // Installing a file the visitor chose replaces the whole database on
        // purpose, so it is not a stale edit and must not be refused as one.
        _revision = Unconditional;

        var installed = await InstallAsync(picked, cancellationToken);
        if (!installed.IsReady)
        {
            // The file on disk was replaced before the payload could be proved
            // good. Storage still holds the previous one, so go back to it rather
            // than leaving the session pointing at nothing — but still report why
            // the import was refused.
            _ready = null;
            _ = await EnsureReadyAsync();
            return installed;
        }

        var persisted = await PersistAsync(cancellationToken);
        _ready = Task.FromResult(persisted.IsSuccess ? installed : new BrowserDatabaseReadiness(false, persisted.Failure));
        return await _ready;
    }

    public async Task<BrowserDatabaseReadiness> ResetAsync(CancellationToken cancellationToken = default)
    {
        // The most destructive operation in the app was the only mutation whose
        // policy was not enforced by the thing that performs it: the About page
        // checked, the recovery screen did not, and a Guest could call it directly.
        if (!await _guard.CanAsync(Permissions.ResetData, cancellationToken))
            return new(false, BrowserDatabaseFailure.Forbidden);

        try
        {
            await _storage.ClearAsync(cancellationToken);
            _revision = Unconditional;   // there is nothing left to be stale against
            await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
                await db.Database.EnsureDeletedAsync(cancellationToken);
            DeleteIfExists(_options.DatabasePath);
            DeleteIfExists(UpgradePath);
            _ready = null;
            return await EnsureReadyAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Resetting the browser database failed");
            return new(false, BrowserDatabaseFailure.ResetFailed);
        }
    }

    private string UpgradePath => _options.DatabasePath + ".upgrade";

    private async Task<BrowserDatabaseReadiness> InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);

        StoredDatabase? stored;
        try
        {
            stored = await _storage.LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Reading the browser database payload failed");
            return new(false, BrowserDatabaseFailure.ReadFailed);
        }

        if (stored is null)
            return await CreateFreshAsync(cancellationToken);

        // From here on this tab's copy is based on that revision, and every write
        // it makes says so.
        _revision = stored.Revision;

        var installed = await InstallAsync(stored, cancellationToken);
        if (!installed.IsReady || stored.Version == _options.SchemaVersion)
            return installed;

        // The payload was upgraded on the way in, so what is in storage is no
        // longer what is on disk. Stamp the new version now rather than waiting
        // for the next mutation, or a reload would upgrade it all over again.
        var persisted = await PersistAsync(cancellationToken);
        return persisted.IsSuccess ? installed : new(false, persisted.Failure);
    }

    /// <summary>Validates a payload, upgrades it when it is behind, and leaves it on disk ready to use.</summary>
    private async Task<BrowserDatabaseReadiness> InstallAsync(StoredDatabase stored, CancellationToken cancellationToken)
    {
        if (stored.Version != _options.SchemaVersion && !SchemaUpgrades.CanUpgradeFrom(stored.Version))
            return new(false, BrowserDatabaseFailure.UnsupportedSchema, stored.Version);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(stored.Data);
        }
        catch (FormatException exception)
        {
            _logger.LogWarning(exception, "Stored browser database is not valid Base64");
            return new(false, BrowserDatabaseFailure.InvalidBase64, stored.Version);
        }

        if (bytes.Length < 100)
            return new(false, BrowserDatabaseFailure.TruncatedPayload, stored.Version);
        if (!bytes.AsSpan(0, SqliteHeader.Length).SequenceEqual(SqliteHeader))
            return new(false, BrowserDatabaseFailure.InvalidSqlite, stored.Version);

        try
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(_options.DatabasePath);
            DeleteIfExists(UpgradePath);

            if (stored.Version == _options.SchemaVersion)
            {
                await File.WriteAllBytesAsync(_options.DatabasePath, bytes, cancellationToken);
            }
            else
            {
                await File.WriteAllBytesAsync(UpgradePath, bytes, cancellationToken);
                try
                {
                    await SchemaUpgrades.ApplyAsync(
                        _factory, UpgradePath, _options.DatabasePath, stored.Version, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Upgrading the stored database from version {Version} failed", stored.Version);
                    DeleteIfExists(_options.DatabasePath);
                    return new(false, BrowserDatabaseFailure.UpgradeFailed, stored.Version);
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    DeleteIfExists(UpgradePath);
                }
            }

            return await VerifyAsync(stored.Version, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DeleteIfExists(_options.DatabasePath);
            _logger.LogWarning(exception, "Stored browser database failed SQLite validation");
            return new(false, BrowserDatabaseFailure.InvalidSqlite, stored.Version);
        }
    }

    /// <summary>
    /// Page structure plus every table the app queries.
    /// <c>PRAGMA quick_check</c> validates pages, not the logical schema, and the
    /// one probe that followed it touched one table of six — so a payload with
    /// the right version stamp and a different schema was declared ready, booted,
    /// and then died inside a page with "no such table" and no way to recover.
    /// </summary>
    private async Task<BrowserDatabaseReadiness> VerifyAsync(int storedVersion, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "PRAGMA quick_check;";
                var check = await command.ExecuteScalarAsync(cancellationToken);
                if (!string.Equals(check?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SQLite quick_check did not return ok.");
            }

            _ = await db.WorkCenters.AsNoTracking().CountAsync(cancellationToken);
            _ = await db.CostCenters.AsNoTracking().CountAsync(cancellationToken);
            _ = await db.WorkPlans.AsNoTracking().CountAsync(cancellationToken);
            _ = await db.Operations.AsNoTracking().CountAsync(cancellationToken);
            _ = await db.ProductionOrders.AsNoTracking().CountAsync(cancellationToken);
            _ = await db.OrderRoutingCenters.AsNoTracking().CountAsync(cancellationToken);
            _ = await db.WorkCenterAbsences.AsNoTracking().CountAsync(cancellationToken);
            _ = await db.PlantSettings.AsNoTracking().CountAsync(cancellationToken);
            return BrowserDatabaseReadiness.Ready;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DeleteIfExists(_options.DatabasePath);
            _logger.LogWarning(exception, "Stored browser database does not carry the expected schema");
            return new(false, BrowserDatabaseFailure.InvalidSqlite, storedVersion);
        }
    }

    /// <summary>
    /// Merges anything still in the write-ahead log into the main file before it
    /// is copied — but only when there is a log. The connection string sets no
    /// <c>journal_mode</c> and the bundled SQLite is not built with
    /// <c>SQLITE_DEFAULT_JOURNAL_MODE=WAL</c>, so the mode is <c>delete</c> and
    /// the checkpoint the architecture doc relies on has always been a no-op.
    /// What actually fixed the ADR-0006 data loss was disposing the context
    /// before reading the file. The checkpoint stays for the day someone turns
    /// WAL on — and then its <c>busy</c> flag is read, because a blocked
    /// checkpoint does not raise and would otherwise be snapshotted as a success
    /// with the last transaction missing.
    /// </summary>
    private async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();

        await using (var mode = connection.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode;";
            var journal = (await mode.ExecuteScalarAsync(cancellationToken))?.ToString();
            if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
                return;
        }

        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) && reader.GetInt32(0) != 0)
            throw new InvalidOperationException(
                "A WAL checkpoint was blocked by another connection, so the snapshot would be incomplete.");
    }

    private async Task<BrowserDatabaseReadiness> CreateFreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(_options.DatabasePath);
            await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
            {
                await db.Database.EnsureCreatedAsync(cancellationToken);
                SeedData.Apply(db);
                await db.SaveChangesAsync(cancellationToken);
            }

            var persisted = await PersistAsync(cancellationToken);
            return persisted.IsSuccess
                ? BrowserDatabaseReadiness.Ready
                : new(false, persisted.Failure);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Creating the fresh browser database failed");
            return new(false, BrowserDatabaseFailure.WriteFailed);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
