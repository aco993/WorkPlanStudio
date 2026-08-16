using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// A real SQLite database in a temp directory, with browser storage faked.
/// EF Core's SQLite provider runs anywhere, so the data layer - and the pages
/// built on it - are testable on a normal .NET host even though the app ships
/// them inside WebAssembly.
/// </summary>
internal sealed class TempDatabaseFiles : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"workplanstudio-{Guid.NewGuid():N}");

    public TempDatabaseFiles() => Directory.CreateDirectory(_directory);

    public TestDbContextFactory CreateFactory(string fileName) => new(Path.Combine(_directory, fileName));

    public BrowserDatabase CreateDatabase(string fileName, FakeStorage storage)
    {
        var path = Path.Combine(_directory, fileName);
        return new BrowserDatabase(
            new TestDbContextFactory(path),
            storage,
            new BrowserDatabaseOptions(path, 3),
            NullLogger<BrowserDatabase>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}

internal sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDbContextFactory(string path) => _options =
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;

    public AppDbContext CreateDbContext() => new(_options);
}

/// <summary>Browser storage, in memory, with switches for the failure paths.</summary>
internal sealed class FakeStorage : IBrowserDatabaseStorage
{
    public StoredDatabase? Stored { get; set; }
    public StoredDatabase? Exported { get; private set; }
    public bool ThrowOnLoad { get; set; }
    public bool ThrowOnSave { get; set; }
    public int SaveCalls { get; private set; }
    public int ClearCalls { get; private set; }

    public ValueTask<StoredDatabase?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnLoad)
            throw new InvalidOperationException("simulated read failure");
        return ValueTask.FromResult(Stored);
    }

    public ValueTask SaveAsync(StoredDatabase database, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        if (ThrowOnSave)
            throw new InvalidOperationException("simulated quota failure");
        Stored = database;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        ClearCalls++;
        Stored = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask ExportAsync(StoredDatabase database, CancellationToken cancellationToken = default)
    {
        Exported = database;
        return ValueTask.CompletedTask;
    }
}
