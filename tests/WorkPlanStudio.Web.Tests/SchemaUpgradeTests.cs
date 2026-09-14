using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The upgrade path from schema 5. Before it existed, bumping the schema version
/// meant every returning visitor lost everything they had entered — the recovery
/// screen offered an export nothing could read, and a reset.
/// </summary>
public sealed class SchemaUpgradeTests
{
    private const string Snapshot =
        """{"PlanNumber":"WP-1","PartNumber":"P-1","PartName":"Shaft","Revision":"A","Operations":[{"OperationNumber":10,"Description":"Turn","WorkCenterId":1,"WorkCenterName":"Lathe","SetupTimeMinutes":10,"TimePerPieceMinutes":2}],"FormatVersion":1}""";

    private static string LegacyRows(string snapshotJson = Snapshot) => $"""
        INSERT INTO WorkCenters (Id, Code, Name, CostCenter, HourlyRate, ParallelCapacity, ShiftPatternKey, IsActive)
        VALUES (1, 'CNC-200', 'Lathe', 'CC-2000', 78.5, 1, 'continuous', 1),
               (2, 'CNC-300', 'Mill',  ' cc-2000 ', 95, 1, 'continuous', 1),
               (3, 'SAW-10',  'Saw',   '', 42, 1, 'continuous', 1);

        INSERT INTO PlantSettings (Id, State, IncludePartialHolidays, AllowExtendedDay, AllowExtendedNight,
                                   SundayWorkAllowed, HolidayWorkAllowed, SundayBoundaryShiftHours, MinimumRestHours, ModifiedUtc)
        VALUES (1, 'NW', 0, 1, 0, 0, 0, 0, 11, '2026-05-20 08:00:00');

        INSERT INTO WorkPlans (Id, PlanNumber, PartNumber, PartName, Revision, Status, LotSize, CreatedUtc, ModifiedUtc)
        VALUES (1, 'WP-1', 'P-1', 'Shaft', 'A', 1, 100, '2026-01-01 00:00:00', '2026-01-01 00:00:00');

        INSERT INTO Operations (Id, WorkPlanId, OperationNumber, Description, WorkCenterId, SetupTimeMinutes, TimePerPieceMinutes, Remarks)
        VALUES (1, 1, 10, 'Turn', 1, 10, 2.25, NULL);

        INSERT INTO ProductionOrders (Id, OrderNumber, WorkPlanId, Quantity, ReleaseUtc, DueUtc, Priority, Status,
                                      RoutingRevision, RoutingSnapshotJson, CreatedUtc, ModifiedUtc)
        VALUES (1, 'PO-1', 1, 100, '2026-06-01 06:00:00', '2026-06-08 06:00:00', 3, 1, 'A', '{snapshotJson}',
                '2026-05-29 09:00:00', '2026-05-29 09:00:00');
        """;

    private static (BrowserDatabase Database, FakeStorage Storage) Legacy(TempDatabaseFiles files, string? rows = null)
    {
        var payload = LegacyDatabaseBuilder.BuildPayload(Path.Join(files.Root, "legacy-source.db"), rows ?? LegacyRows());
        var storage = new FakeStorage { Stored = new StoredDatabase(payload, 5) };
        return (files.CreateDatabase("upgraded.db", storage), storage);
    }

    [Fact]
    public async Task A_schema_5_database_is_upgraded_instead_of_being_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, storage) = Legacy(files);

        var readiness = await database.EnsureReadyAsync();

        Assert.True(readiness.IsReady);
        Assert.Equal(BrowserDatabaseFailure.None, readiness.Failure);

        // The upgraded payload is stamped with the new version, so a reload does
        // not run the upgrade a second time.
        Assert.Equal(SchemaUpgrades.CurrentVersion, storage.Stored!.Version);

        await using var db = await database.CreateContextAsync(cancellationToken);
        Assert.Equal(3, await db.WorkCenters.CountAsync(cancellationToken));
        Assert.Equal(1, await db.WorkPlans.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Operations.CountAsync(cancellationToken));
        Assert.Equal(1, await db.ProductionOrders.CountAsync(cancellationToken));
        Assert.Equal("NW", (await db.PlantSettings.SingleAsync(cancellationToken)).State);
    }

    [Fact]
    public async Task Repeated_cost_centre_strings_become_one_row_per_distinct_code()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, _) = Legacy(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);

        // 'CC-2000' and ' cc-2000 ' were two strings and are one cost centre;
        // the empty one is not a cost centre at all.
        var costCenter = Assert.Single(await db.CostCenters.ToListAsync(cancellationToken));
        Assert.Equal("CC-2000", costCenter.Code);

        var centers = await db.WorkCenters.OrderBy(w => w.Code).ToListAsync(cancellationToken);
        Assert.Equal(costCenter.Id, centers[0].CostCenterId);   // CNC-200
        Assert.Equal(costCenter.Id, centers[1].CostCenterId);   // CNC-300, the differently-spelled one
        Assert.Null(centers[2].CostCenterId);                   // SAW-10 had no cost centre
    }

    [Fact]
    public async Task The_released_order_keeps_its_snapshot_and_gains_a_routing_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, _) = Legacy(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        var order = await db.ProductionOrders.Include(o => o.RoutingCenters).SingleAsync(cancellationToken);

        Assert.Equal("PO-1", order.OrderNumber);
        Assert.Equal(Snapshot, order.RoutingSnapshotJson);

        // The ids inside the snapshot still resolve, which is why the upgrade
        // preserves primary keys rather than letting the database assign new ones.
        var routing = Assert.Single(order.RoutingCenters);
        Assert.Equal(1, routing.WorkCenterId);

        // And the dates that were called "Utc" and never were arrive unchanged.
        Assert.Equal(new DateTime(2026, 6, 1, 6, 0, 0), order.ReleaseLocal);
        Assert.Equal(new DateTime(2026, 6, 8, 6, 0, 0), order.DueLocal);
    }

    [Fact]
    public async Task Decimals_survive_the_upgrade_and_land_on_a_text_column()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, _) = Legacy(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        Assert.Equal(78.5m, (await db.WorkCenters.SingleAsync(w => w.Code == "CNC-200", cancellationToken)).HourlyRate);
        Assert.Equal(2.25m, (await db.Operations.SingleAsync(cancellationToken)).TimePerPieceMinutes);

        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT typeof(HourlyRate) FROM WorkCenters WHERE Code = 'CNC-200';";
        Assert.Equal("text", (await command.ExecuteScalarAsync(cancellationToken))?.ToString());
    }

    [Fact]
    public async Task An_order_whose_snapshot_names_a_missing_work_centre_still_upgrades()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        const string orphaned =
            """{"PlanNumber":"WP-1","PartNumber":"P-1","PartName":"Shaft","Revision":"A","Operations":[{"OperationNumber":10,"Description":"Turn","WorkCenterId":99,"WorkCenterName":"Gone","SetupTimeMinutes":10,"TimePerPieceMinutes":2}],"FormatVersion":1}""";
        var (database, _) = Legacy(files, LegacyRows(orphaned));

        // Data that cannot be cleanly migrated must not take the whole upgrade
        // down with it: the row is kept, the index entry is simply not written,
        // and the scheduler reports the order the way it always did.
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        Assert.Equal(1, await db.ProductionOrders.CountAsync(cancellationToken));
        Assert.Equal(0, await db.OrderRoutingCenters.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task A_version_with_no_upgrade_step_is_preserved_rather_than_guessed_at()
    {
        using var files = new TempDatabaseFiles();
        var payload = LegacyDatabaseBuilder.BuildPayload(Path.Join(files.Root, "old.db"), LegacyRows());

        var tooOld = new FakeStorage { Stored = new StoredDatabase(payload, 4) };
        var oldResult = await files.CreateDatabase("too-old.db", tooOld).EnsureReadyAsync();
        Assert.Equal(BrowserDatabaseFailure.UnsupportedSchema, oldResult.Failure);
        Assert.Equal(4, oldResult.StoredVersion);
        Assert.Equal(0, tooOld.SaveCalls);

        // A payload from a *newer* deployment served out of a stale cache is the
        // other direction, and must be just as untouched.
        var tooNew = new FakeStorage { Stored = new StoredDatabase(payload, SchemaUpgrades.CurrentVersion + 1) };
        var newResult = await files.CreateDatabase("too-new.db", tooNew).EnsureReadyAsync();
        Assert.Equal(BrowserDatabaseFailure.UnsupportedSchema, newResult.Failure);
        Assert.Equal(0, tooNew.SaveCalls);
    }

    [Fact]
    public async Task A_payload_carrying_the_right_version_but_the_wrong_schema_is_refused()
    {
        using var files = new TempDatabaseFiles();

        // A valid SQLite file that passes quick_check and holds one of the eight
        // tables. The old probe counted WorkCenters and nothing else, so this
        // booted and then died inside a page with "no such table".
        var path = Path.Join(files.Root, "partial.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"WorkCenters\" (\"Id\" INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var payload = Convert.ToBase64String(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        File.Delete(path);

        var storage = new FakeStorage { Stored = new StoredDatabase(payload, SchemaUpgrades.CurrentVersion) };
        var readiness = await files.CreateDatabase("partial-target.db", storage).EnsureReadyAsync();

        Assert.False(readiness.IsReady);
        Assert.Equal(BrowserDatabaseFailure.InvalidSqlite, readiness.Failure);
        Assert.Equal(0, storage.SaveCalls);
    }

    [Fact]
    public async Task An_exported_payload_can_be_imported_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("round-trip.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var centers = new WorkCenterService(database);
        Assert.True((await centers.SaveAsync(
            new WorkCenter { Code = "IMP-1", Name = "Imported", ParallelCapacity = 1 }, cancellationToken)).IsSuccess);

        Assert.True((await database.ExportStoredPayloadAsync(cancellationToken)).IsSuccess);
        var exported = storage.Exported!;

        // A fresh browser: seed data only, then the exported file is installed.
        var secondStorage = new FakeStorage { ToImport = exported };
        var second = files.CreateDatabase("imported.db", secondStorage);
        Assert.True((await second.EnsureReadyAsync()).IsReady);
        Assert.DoesNotContain(
            await new WorkCenterService(second).GetAllAsync(cancellationToken),
            center => center.Code == "IMP-1");

        var imported = await second.ImportAsync(cancellationToken);

        Assert.True(imported.IsReady);
        Assert.Contains(
            await new WorkCenterService(second).GetAllAsync(cancellationToken),
            center => center.Code == "IMP-1");
    }

    [Fact]
    public async Task A_schema_5_export_can_be_imported_and_is_upgraded_on_the_way_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var payload = LegacyDatabaseBuilder.BuildPayload(Path.Join(files.Root, "legacy-export.db"), LegacyRows());

        var storage = new FakeStorage { ToImport = new StoredDatabase(payload, 5) };
        var database = files.CreateDatabase("import-upgrade.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        Assert.True((await database.ImportAsync(cancellationToken)).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        Assert.Equal("CC-2000", (await db.CostCenters.SingleAsync(cancellationToken)).Code);
        Assert.Equal(SchemaUpgrades.CurrentVersion, storage.Stored!.Version);
    }

    [Fact]
    public async Task A_rejected_import_leaves_the_existing_database_usable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage { ToImport = new StoredDatabase("not base64 at all", SchemaUpgrades.CurrentVersion) };
        var database = files.CreateDatabase("bad-import.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var before = await new WorkCenterService(database).GetAllAsync(cancellationToken);

        var result = await database.ImportAsync(cancellationToken);

        Assert.False(result.IsReady);
        Assert.Equal(BrowserDatabaseFailure.InvalidBase64, result.Failure);
        Assert.Equal(
            before.Select(center => center.Code),
            (await new WorkCenterService(database).GetAllAsync(cancellationToken)).Select(center => center.Code));
    }
}
