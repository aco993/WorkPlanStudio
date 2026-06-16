using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace WorkPlanStudio.Data;

/// <summary>
/// Bridges EF Core's SQLite file (which lives in the browser's in-memory file
/// system) with persistent <c>localStorage</c>. The database is loaded once on
/// startup, seeded on first run, and written back after every change — so data
/// survives page reloads even though there is no server.
/// </summary>
public sealed class BrowserDatabase
{
    // Path inside the WebAssembly virtual file system.
    private const string DbPath = "/data/workplan.db";

    // Bump when the schema OR the seed content changes so a stale stored database
    // is discarded and re-seeded instead of being reused.
    // v2: enriched the sample data to seven released plans so the scheduler has a
    //     non-trivial problem (dispatch rule and seed visibly change the result).
    private const int SchemaVersion = 2;

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IJSRuntime _js;
    private Task? _ready;

    public BrowserDatabase(IDbContextFactory<AppDbContext> factory, IJSRuntime js)
    {
        _factory = factory;
        _js = js;
    }

    /// <summary>Idempotent: runs initialization exactly once per app session.</summary>
    public Task EnsureReadyAsync() => _ready ??= InitializeAsync();

    /// <summary>Creates a fresh, ready-to-use context (caller disposes it).</summary>
    public async Task<AppDbContext> CreateContextAsync()
    {
        await EnsureReadyAsync();
        return await _factory.CreateDbContextAsync();
    }

    /// <summary>Writes the current SQLite file back to browser storage.</summary>
    public async Task PersistAsync()
    {
        var bytes = await File.ReadAllBytesAsync(DbPath);
        await _js.InvokeVoidAsync("workplanDb.save", Convert.ToBase64String(bytes), SchemaVersion);
    }

    /// <summary>Deletes all stored data and re-creates the sample database.</summary>
    public async Task ResetAsync()
    {
        await _js.InvokeVoidAsync("workplanDb.clear");
        if (File.Exists(DbPath))
            File.Delete(DbPath);
        _ready = null;
        await EnsureReadyAsync();
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory("/data");

        var stored = await _js.InvokeAsync<StoredDatabase?>("workplanDb.load");
        if (stored is { Version: SchemaVersion, Data.Length: > 0 })
            await File.WriteAllBytesAsync(DbPath, Convert.FromBase64String(stored.Data));

        await using var db = await _factory.CreateDbContextAsync();
        var createdFresh = await db.Database.EnsureCreatedAsync();
        if (createdFresh)
        {
            SeedData.Apply(db);
            await db.SaveChangesAsync();
            await PersistAsync();
        }
    }

    // Shape returned by workplanDb.load (camelCase from JS is matched case-insensitively).
    private sealed record StoredDatabase(string Data, int Version);
}
