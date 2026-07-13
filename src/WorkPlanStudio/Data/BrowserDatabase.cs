using System.Text;
using Microsoft.EntityFrameworkCore;

namespace WorkPlanStudio.Data;

/// <summary>
/// Owns the explicit boundary between SQLite in the browser file system and the
/// versioned Base64 payload in localStorage. Corrupt or incompatible payloads
/// are preserved for export until the user explicitly resets them.
/// </summary>
public sealed class BrowserDatabase
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IBrowserDatabaseStorage _storage;
    private readonly BrowserDatabaseOptions _options;
    private readonly ILogger<BrowserDatabase> _logger;
    private Task<BrowserDatabaseReadiness>? _ready;

    public BrowserDatabase(
        IDbContextFactory<AppDbContext> factory,
        IBrowserDatabaseStorage storage,
        BrowserDatabaseOptions options,
        ILogger<BrowserDatabase> logger)
    {
        _factory = factory;
        _storage = storage;
        _options = options;
        _logger = logger;
    }

    public Task<BrowserDatabaseReadiness> EnsureReadyAsync() => _ready ??= InitializeAsync();

    public async Task<AppDbContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var readiness = await EnsureReadyAsync();
        if (!readiness.IsReady)
            throw new BrowserDatabaseUnavailableException(readiness);

        return await _factory.CreateDbContextAsync(cancellationToken);
    }

    public async Task<BrowserStorageResult> PersistAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // SQLite WASM uses WAL mode. Snapshotting only the main .db file before
            // a checkpoint loses committed schema/data that still lives in -wal.
            await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
            {
                await db.Database.OpenConnectionAsync(cancellationToken);
                await using var checkpoint = db.Database.GetDbConnection().CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            }

            var bytes = await File.ReadAllBytesAsync(_options.DatabasePath, cancellationToken);
            await _storage.SaveAsync(
                new StoredDatabase(Convert.ToBase64String(bytes), _options.SchemaVersion),
                cancellationToken);
            return BrowserStorageResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Persisting the browser database failed");
            return new(false, BrowserDatabaseFailure.WriteFailed);
        }
    }

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

    public async Task<BrowserDatabaseReadiness> ResetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _storage.ClearAsync(cancellationToken);
            await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
                await db.Database.EnsureDeletedAsync(cancellationToken);
            DeleteIfExists(_options.DatabasePath);
            DeleteIfExists(ImportPath);
            _ready = null;
            return await EnsureReadyAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Resetting the browser database failed");
            return new(false, BrowserDatabaseFailure.ResetFailed);
        }
    }

    private string ImportPath => _options.DatabasePath + ".import";

    private async Task<BrowserDatabaseReadiness> InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);

        StoredDatabase? stored;
        try
        {
            stored = await _storage.LoadAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Reading the browser database payload failed");
            return new(false, BrowserDatabaseFailure.ReadFailed);
        }

        if (stored is null)
            return await CreateFreshAsync();

        if (stored.Version != _options.SchemaVersion)
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
            DeleteIfExists(_options.DatabasePath);
            await File.WriteAllBytesAsync(_options.DatabasePath, bytes);

            await using var db = await _factory.CreateDbContextAsync();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var check = await command.ExecuteScalarAsync();
            if (!string.Equals(check?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite quick_check did not return ok.");
            _ = await db.WorkCenters.AsNoTracking().CountAsync();
            return BrowserDatabaseReadiness.Ready;
        }
        catch (Exception exception)
        {
            DeleteIfExists(_options.DatabasePath);
            _logger.LogWarning(exception, "Stored browser database failed SQLite validation");
            return new(false, BrowserDatabaseFailure.InvalidSqlite, stored.Version);
        }
    }

    private async Task<BrowserDatabaseReadiness> CreateFreshAsync()
    {
        try
        {
            DeleteIfExists(_options.DatabasePath);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                SeedData.Apply(db);
                await db.SaveChangesAsync();
            }

            var persisted = await PersistAsync();
            return persisted.IsSuccess
                ? BrowserDatabaseReadiness.Ready
                : new(false, persisted.Failure);
        }
        catch (Exception exception)
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
