using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;

namespace WorkPlanStudio.Web.Tests;

public sealed class BrowserDatabaseTests
{
    [Fact]
    public async Task Save_then_new_database_instance_restores_the_committed_entity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var first = files.CreateDatabase("first.db", storage);
        Assert.True((await first.EnsureReadyAsync()).IsReady);

        var service = new WorkCenterService(first);
        var saved = await service.SaveAsync(new WorkCenter
        {
            Code = "RELOAD-1",
            Name = "Reload regression",
            HourlyRate = 10,
            ParallelCapacity = 2
        }, cancellationToken);
        Assert.True(saved.IsSuccess);

        var reloaded = files.CreateDatabase("reloaded.db", storage);
        Assert.True((await reloaded.EnsureReadyAsync()).IsReady);
        var centers = await new WorkCenterService(reloaded).GetAllAsync(cancellationToken);

        var restored = Assert.Single(centers, center => center.Code == "RELOAD-1");
        Assert.Equal(2, restored.ParallelCapacity);
    }

    [Theory]
    [InlineData("not base64", BrowserDatabaseFailure.InvalidBase64)]
    [InlineData("AA==", BrowserDatabaseFailure.TruncatedPayload)]
    public async Task Invalid_or_truncated_payload_requires_recovery_without_overwrite(
        string payload,
        BrowserDatabaseFailure expected)
    {
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage { Stored = new(payload, 3) };

        var result = await files.CreateDatabase("invalid.db", storage).EnsureReadyAsync();

        Assert.False(result.IsReady);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(0, storage.SaveCalls);
        Assert.Equal(payload, storage.Stored!.Data);
    }

    [Fact]
    public async Task Valid_base64_that_is_not_sqlite_requires_recovery()
    {
        using var files = new TempDatabaseFiles();
        var payload = Convert.ToBase64String(Enumerable.Repeat((byte)42, 200).ToArray());
        var storage = new FakeStorage { Stored = new(payload, 3) };

        var result = await files.CreateDatabase("not-sqlite.db", storage).EnsureReadyAsync();

        Assert.Equal(BrowserDatabaseFailure.InvalidSqlite, result.Failure);
        Assert.Equal(0, storage.SaveCalls);
    }

    [Fact]
    public async Task Unsupported_schema_is_preserved_and_can_be_exported_before_reset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var original = new StoredDatabase("legacy-payload", 2);
        var storage = new FakeStorage { Stored = original };
        var database = files.CreateDatabase("schema.db", storage);

        var readiness = await database.EnsureReadyAsync();
        var exported = await database.ExportStoredPayloadAsync(cancellationToken);

        Assert.Equal(BrowserDatabaseFailure.UnsupportedSchema, readiness.Failure);
        Assert.Equal(2, readiness.StoredVersion);
        Assert.Equal(0, storage.SaveCalls);
        Assert.True(exported.IsSuccess);
        Assert.Equal(original, storage.Exported);

        var reset = await database.ResetAsync(cancellationToken);
        Assert.True(reset.IsReady);
        Assert.True(storage.ClearCalls > 0);
        Assert.NotEqual(original, storage.Stored);
    }

    [Fact]
    public async Task Storage_read_and_write_failures_have_typed_outcomes()
    {
        using var files = new TempDatabaseFiles();
        var readFailure = new FakeStorage { ThrowOnLoad = true };
        var readResult = await files.CreateDatabase("read.db", readFailure).EnsureReadyAsync();
        Assert.Equal(BrowserDatabaseFailure.ReadFailed, readResult.Failure);

        var writeFailure = new FakeStorage { ThrowOnSave = true };
        var writeResult = await files.CreateDatabase("write.db", writeFailure).EnsureReadyAsync();
        Assert.Equal(BrowserDatabaseFailure.WriteFailed, writeResult.Failure);
    }

    [Fact]
    public async Task Service_reports_storage_write_failure_instead_of_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("quota.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        storage.ThrowOnSave = true;

        var result = await new WorkCenterService(database).SaveAsync(new WorkCenter
        {
            Code = "QUOTA",
            Name = "Quota failure",
            ParallelCapacity = 1
        }, cancellationToken);

        Assert.Equal(ApplicationResultStatus.PersistenceFailed, result.Status);
    }

    [Fact]
    public async Task Relational_constraints_reject_invalid_capacity_and_duplicate_operation_number()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var factory = files.CreateFactory("constraints.db");
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        db.WorkCenters.Add(new WorkCenter
        {
            Code = "BAD-CAP",
            Name = "Bad",
            ParallelCapacity = 0
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken));
        db.ChangeTracker.Clear();

        var center = new WorkCenter { Code = "WC-X", Name = "Center", ParallelCapacity = 1 };
        db.WorkPlans.Add(new WorkPlan
        {
            PlanNumber = "WP-X",
            PartName = "Part",
            LotSize = 1,
            Operations =
            [
                new Operation { OperationNumber = 10, Description = "A", WorkCenter = center },
                new Operation { OperationNumber = 10, Description = "B", WorkCenter = center }
            ]
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task Services_return_conflict_for_duplicates_and_not_found_for_stale_update()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("results.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var centers = new WorkCenterService(database);

        var duplicate = await centers.SaveAsync(new WorkCenter
        {
            Code = "saw-10",
            Name = "Duplicate",
            ParallelCapacity = 1
        }, cancellationToken);
        Assert.Equal(ApplicationResultStatus.Conflict, duplicate.Status);

        var stalePlan = new WorkPlan
        {
            Id = int.MaxValue,
            PlanNumber = "WP-MISSING",
            PartName = "Missing",
            LotSize = 1,
            Operations =
            [
                new Operation { OperationNumber = 10, Description = "Missing", WorkCenterId = 1 }
            ]
        };
        var notFound = await new WorkPlanService(database).UpdateAsync(stalePlan, cancellationToken);
        Assert.Equal(ApplicationResultStatus.NotFound, notFound.Status);
    }

    [Fact]
    public async Task Crud_services_preserve_aggregate_updates_and_released_plan_guards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("crud.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var centers = new WorkCenterService(database);
        var plans = new WorkPlanService(database);
        var saw = Assert.Single(await centers.GetAllAsync(cancellationToken), center => center.Code == "SAW-10");

        var plan = new WorkPlan
        {
            PlanNumber = " WP-CRUD ",
            PartName = " CRUD part ",
            LotSize = 10,
            Status = WorkPlanStatus.Draft,
            Operations =
            [
                new Operation
                {
                    OperationNumber = 10,
                    Description = " First ",
                    WorkCenterId = saw.Id,
                    SetupTimeMinutes = 1,
                    TimePerPieceMinutes = 2
                }
            ]
        };
        var created = await plans.CreateAsync(plan, cancellationToken);
        Assert.True(created.IsSuccess);

        plan.PartName = "Updated";
        plan.Operations =
        [
            new Operation { OperationNumber = 10, Description = "A", WorkCenterId = saw.Id },
            new Operation { OperationNumber = 20, Description = "B", WorkCenterId = saw.Id }
        ];
        var updated = await plans.UpdateAsync(plan, cancellationToken);
        Assert.True(updated.IsSuccess);
        var restored = await plans.GetAsync(plan.Id, cancellationToken);
        Assert.Equal("Updated", restored!.PartName);
        Assert.Equal(new[] { 10, 20 }, restored.Operations.Select(operation => operation.OperationNumber).Order().ToArray());

        var usage = await centers.GetUsageCountsAsync(cancellationToken);
        Assert.True(usage[saw.Id] >= 2);
        Assert.Equal(ApplicationResultStatus.Conflict, (await centers.DeleteAsync(saw.Id, cancellationToken)).Status);

        saw.IsActive = false;
        Assert.Equal(ApplicationResultStatus.Conflict, (await centers.SaveAsync(saw, cancellationToken)).Status);

        Assert.True((await plans.DeleteAsync(plan.Id, cancellationToken)).IsSuccess);
        Assert.Null(await plans.GetAsync(plan.Id, cancellationToken));
    }

    private sealed class TempDatabaseFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"workplanstudio-{Guid.NewGuid():N}");

        public TempDatabaseFiles() => Directory.CreateDirectory(_directory);

        public TestDbContextFactory CreateFactory(string fileName) =>
            new(Path.Combine(_directory, fileName));

        public BrowserDatabase CreateDatabase(string fileName, FakeStorage storage)
        {
            var path = Path.Combine(_directory, fileName);
            return new BrowserDatabase(
                new TestDbContextFactory(path),
                storage,
                new BrowserDatabaseOptions(path, 3),
                NullLogger<BrowserDatabase>.Instance);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(string path) => _options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    private sealed class FakeStorage : IBrowserDatabaseStorage
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
}
