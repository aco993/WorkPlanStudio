using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The step from schema 6 to schema 7. Three settings columns were added, so a
/// returning visitor's database has to gain them without losing anything — and
/// a stored ten-hour rest, which schema 6 could not qualify with a § 5 (2)
/// sector, has to land somewhere the new CHECK constraint accepts.
/// </summary>
public sealed class SchemaUpgradeFromSixTests
{
    private const string Snapshot =
        """{"PlanNumber":"WP-7","PartNumber":"P-7","PartName":"Flange","Revision":"B","Operations":[{"OperationNumber":10,"Description":"Mill","WorkCenterId":1,"WorkCenterName":"Mill","SetupTimeMinutes":15,"TimePerPieceMinutes":3}],"FormatVersion":1}""";

    private static string Rows(int minimumRestHours = 11) => $"""
        INSERT INTO CostCenters (Id, Code, Name, Description, IsActive)
        VALUES (7, 'CC-7000', 'Machining', 'Cell 7', 1);

        INSERT INTO WorkCenters (Id, Code, Name, CostCenterId, HourlyRate, ParallelCapacity, ShiftPatternKey, IsActive)
        VALUES (1, 'MIL-10', 'Mill', 7, '95.5', 2, 'two-shift', 1),
               (2, 'SAW-20', 'Saw', NULL, '42', 1, 'one-shift', 1);

        INSERT INTO WorkCenterAbsences (Id, WorkCenterId, Start, "End", Kind, Label)
        VALUES (1, 1, '2026-07-01 06:00:00', '2026-07-03 06:00:00', 1, 'Spindle service');

        INSERT INTO PlantSettings (Id, State, IncludePartialHolidays, AllowExtendedDay, AllowExtendedNight,
                                   SundayWorkAllowed, HolidayWorkAllowed, SundayBoundaryShiftHours, MinimumRestHours, ModifiedUtc)
        VALUES (1, 'BY', 1, 1, 0, 1, 0, 4, {minimumRestHours}, '2026-06-01 08:00:00');

        INSERT INTO WorkPlans (Id, PlanNumber, PartNumber, PartName, Revision, Status, LotSize, CreatedUtc, ModifiedUtc)
        VALUES (1, 'WP-7', 'P-7', 'Flange', 'B', 1, 250, '2026-01-01 00:00:00', '2026-01-01 00:00:00');

        INSERT INTO Operations (Id, WorkPlanId, OperationNumber, Description, WorkCenterId, SetupTimeMinutes, TimePerPieceMinutes, Remarks)
        VALUES (1, 1, 10, 'Mill', 1, '15', '3.75', NULL);

        INSERT INTO ProductionOrders (Id, OrderNumber, WorkPlanId, Quantity, ReleaseLocal, DueLocal, Priority, Status,
                                      RoutingRevision, RoutingSnapshotJson, CreatedUtc, ModifiedUtc)
        VALUES (1, 'PO-7', 1, 250, '2026-06-01 06:00:00', '2026-06-12 06:00:00', 2, 1, 'B', '{Snapshot}',
                '2026-05-20 09:00:00', '2026-05-20 09:00:00');

        INSERT INTO OrderRoutingCenters (ProductionOrderId, WorkCenterId) VALUES (1, 1);
        """;

    private static (BrowserDatabase Database, FakeStorage Storage) Legacy(TempDatabaseFiles files, int minimumRestHours = 11)
    {
        var payload = LegacyV6DatabaseBuilder.BuildPayload(Path.Join(files.Root, "v6-source.db"), Rows(minimumRestHours));
        var storage = new FakeStorage { Stored = new StoredDatabase(payload, 6) };
        return (files.CreateDatabase("v6-upgraded.db", storage), storage);
    }

    [Fact]
    public void Schema_six_is_one_behind_and_still_upgradable()
    {
        Assert.Equal(7, SchemaUpgrades.CurrentVersion);
        Assert.True(SchemaUpgrades.CanUpgradeFrom(6));
        Assert.True(SchemaUpgrades.CanUpgradeFrom(5));
        Assert.False(SchemaUpgrades.CanUpgradeFrom(4));
    }

    [Fact]
    public async Task A_schema_6_database_keeps_everything_it_had()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, storage) = Legacy(files);

        var readiness = await database.EnsureReadyAsync();

        Assert.True(readiness.IsReady);
        Assert.Equal(SchemaUpgrades.CurrentVersion, storage.Stored!.Version);

        await using var db = await database.CreateContextAsync(cancellationToken);
        Assert.Equal("CC-7000", (await db.CostCenters.SingleAsync(cancellationToken)).Code);
        Assert.Equal(2, await db.WorkCenters.CountAsync(cancellationToken));
        Assert.Equal(7, (await db.WorkCenters.SingleAsync(w => w.Code == "MIL-10", cancellationToken)).CostCenterId);
        Assert.Equal(95.5m, (await db.WorkCenters.SingleAsync(w => w.Code == "MIL-10", cancellationToken)).HourlyRate);
        Assert.Equal(3.75m, (await db.Operations.SingleAsync(cancellationToken)).TimePerPieceMinutes);
        Assert.Equal("Spindle service", (await db.WorkCenterAbsences.SingleAsync(cancellationToken)).Label);

        var order = await db.ProductionOrders.Include(o => o.RoutingCenters).SingleAsync(cancellationToken);
        Assert.Equal(Snapshot, order.RoutingSnapshotJson);
        Assert.Equal(new DateTime(2026, 6, 1, 6, 0, 0), order.ReleaseLocal);
        Assert.Equal(1, Assert.Single(order.RoutingCenters).WorkCenterId);
    }

    [Fact]
    public async Task The_settings_the_old_schema_could_state_survive_and_the_new_ones_claim_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, _) = Legacy(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        var settings = await db.PlantSettings.SingleAsync(cancellationToken);

        Assert.Equal("BY", settings.State);
        Assert.True(settings.IncludePartialHolidays);
        Assert.True(settings.SundayWorkAllowed);
        Assert.Equal(4, settings.SundayBoundaryShiftHours);

        // Nothing was declared, so nothing is claimed: the shorter reference
        // period, no § 5 (2) sector, and the rota that means "no rota".
        Assert.Equal(AveragingWindow.TwentyFourWeeks, settings.AveragingWindow);
        Assert.Equal(RestExceptionSector.None, settings.RestExceptionSector);
        Assert.Equal(1, settings.SundayRotationWeeks);

        // A rota of one is the honest reading and it must not divide by zero.
        Assert.Equal(1, settings.ToRules().SundayRotationWeeks);
    }

    [Fact]
    public async Task A_stored_ten_hour_rest_comes_back_as_eleven_rather_than_failing_the_upgrade()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, _) = Legacy(files, minimumRestHours: 10);

        // Schema 6 allowed 10 h with no sector to justify it; schema 7 does not.
        // Refusing the row would mean refusing the whole database, so the value
        // goes back to the § 5 (1) rest and the planner can take it again — this
        // time by naming the sector.
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        var settings = await db.PlantSettings.SingleAsync(cancellationToken);
        Assert.Equal(11, settings.MinimumRestHours);
        Assert.Equal(RestExceptionSector.None, settings.RestExceptionSector);
        Assert.Equal(TimeSpan.FromHours(11), settings.ToRules().MinimumRest);
    }

    [Fact]
    public async Task A_schema_6_export_can_be_imported_and_is_upgraded_on_the_way_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var payload = LegacyV6DatabaseBuilder.BuildPayload(Path.Join(files.Root, "v6-export.db"), Rows());

        var storage = new FakeStorage { ToImport = new StoredDatabase(payload, 6) };
        var database = files.CreateDatabase("v6-import.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        Assert.True((await database.ImportAsync(cancellationToken)).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        Assert.Equal("CC-7000", (await db.CostCenters.SingleAsync(cancellationToken)).Code);
        Assert.Equal(SchemaUpgrades.CurrentVersion, storage.Stored!.Version);
    }
}
