using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Remote;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The one-way import, over the model the server now speaks.
/// <para>
/// A pull replaces the local copy wholesale, so the questions worth asking are
/// about what it refuses to make up: a cost centre the server does not have, and
/// a routing-index row for a machine the pull did not bring. Both would satisfy
/// a foreign key and both would be fiction.
/// </para>
/// </summary>
public sealed class RemoteMasterDataSyncTests
{
    private static readonly Uri Api = new("https://api.example.invalid/");

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pull_writes_the_server_rows_with_the_server_keys()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("pull.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var sync = new RemoteMasterDataSync(Client(Plant()), database);

        var result = await sync.PullAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(new RemotePullSummary(2, 2, 1, 1), result.Value);

        await using var db = await database.CreateContextAsync(Ct);
        var machining = await db.CostCenters.SingleAsync(c => c.Code == "CC-2000", Ct);
        Assert.Equal(20, machining.Id);
        Assert.Equal("Machining", machining.Name);

        var lathe = await db.WorkCenters.Include(w => w.CostCenter).SingleAsync(w => w.Code == "CNC-200", Ct);
        Assert.Equal(machining.Id, lathe.CostCenterId);
        Assert.Equal("Machining", lathe.CostCenter?.Name);

        // Server identifiers are preserved: a released order's frozen routing
        // names work centres by id, so renumbering on import would point every
        // snapshot at the wrong machine.
        Assert.Equal(200, lathe.Id);
    }

    [Fact]
    public async Task A_work_centre_naming_a_cost_centre_the_server_did_not_send_arrives_unassigned()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("orphan-cost-centre.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var plant = Plant();
        plant.WorkCenters[0] = plant.WorkCenters[0] with
        {
            CostCenterId = 999,
            CostCenterCode = "CC-9999",
            CostCenterName = "Not in the catalogue"
        };

        var result = await new RemoteMasterDataSync(Client(plant), database).PullAsync(Ct);

        Assert.True(result.IsSuccess);
        await using var db = await database.CreateContextAsync(Ct);

        // "Not assigned" is a state Controlling recognises. Creating CC-9999 from
        // the display fields to satisfy the foreign key would put a cost centre
        // in the planner's catalogue that nobody owns.
        Assert.Null((await db.WorkCenters.SingleAsync(w => w.Code == "SAW-10", Ct)).CostCenterId);
        Assert.Equal(2, await db.CostCenters.CountAsync(Ct));
        Assert.False(await db.CostCenters.AnyAsync(c => c.Code == "CC-9999", Ct));
    }

    [Fact]
    public async Task A_released_order_brings_the_index_its_snapshot_implies()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("routing-index.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var result = await new RemoteMasterDataSync(Client(Plant()), database).PullAsync(Ct);
        Assert.True(result.IsSuccess);

        await using var db = await database.CreateContextAsync(Ct);
        var rows = await db.OrderRoutingCenters.Select(r => r.WorkCenterId).OrderBy(id => id).ToListAsync(Ct);

        // Rebuilt from the blob rather than pulled, because it is what the local
        // guards read: without it a planner could retire a machine a released
        // order still needs, on data that came from a server which forbids it.
        Assert.Equal([100, 200], rows);
    }

    [Fact]
    public async Task A_snapshot_naming_a_work_centre_the_pull_did_not_bring_costs_the_order_nothing()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("orphan-snapshot.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var plant = Plant();
        plant.Orders[0] = plant.Orders[0] with
        {
            RoutingSnapshotJson = Snapshot(new RoutingSnapshotOperation(10, "Vanished", 777, "Gone", 1m, 1m))
        };

        var result = await new RemoteMasterDataSync(Client(plant), database).PullAsync(Ct);

        Assert.True(result.IsSuccess);
        await using var db = await database.CreateContextAsync(Ct);

        // The order is kept and still reported the way it always was; an index row
        // pointing at a machine that is not there would fail the whole write.
        Assert.Equal(1, await db.ProductionOrders.CountAsync(Ct));
        Assert.Empty(await db.OrderRoutingCenters.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_planning_dates_survive_the_wire_as_wall_clock_readings()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("wall-clock.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var result = await new RemoteMasterDataSync(Client(Plant()), database).PullAsync(Ct);
        Assert.True(result.IsSuccess);

        await using var db = await database.CreateContextAsync(Ct);
        var order = await db.ProductionOrders.SingleAsync(Ct);

        // System.Text.Json reads an offset-bearing string back as Local, which
        // would make the stored reading depend on the visitor's own zone.
        Assert.Equal(new DateTime(2026, 6, 1, 6, 0, 0), order.ReleaseLocal);
        Assert.Equal(DateTimeKind.Unspecified, order.ReleaseLocal.Kind);
        Assert.Equal(new DateTime(2026, 6, 8, 6, 0, 0), order.DueLocal);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_leaves_the_local_copy_alone()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("unreachable.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using (var seed = await database.CreateContextAsync(Ct))
        {
            seed.CostCenters.Add(new CostCenter { Code = "CC-LOCAL", Name = "Local only" });
            await seed.SaveChangesAsync(Ct);
        }

        var offline = new ApiClient(new HttpClient(new FailingHandler()) { BaseAddress = Api });
        var result = await new RemoteMasterDataSync(offline, database).PullAsync(Ct);

        Assert.Equal(ApplicationResultStatus.PersistenceFailed, result.Status);
        await using var db = await database.CreateContextAsync(Ct);
        Assert.True(await db.CostCenters.AnyAsync(c => c.Code == "CC-LOCAL", Ct));
    }

    // ----- support -----

    /// <summary>A small plant, in the shape the API returns it.</summary>
    private static PlantPayload Plant()
    {
        var snapshot = Snapshot(
            new RoutingSnapshotOperation(10, "Cut to length", 100, "Cut-off Saw", 10m, 0.8m),
            new RoutingSnapshotOperation(20, "Turn", 200, "CNC Turning Center", 35m, 4.2m));

        return new PlantPayload
        {
            CostCenters =
            [
                new CostCenterDto(10, "CC-1000", "Sawing", null, true, "stamp-cc-10"),
                new CostCenterDto(20, "CC-2000", "Machining", "The plant's most expensive hours.", true, "stamp-cc-20")
            ],
            WorkCenters =
            [
                new WorkCenterDto(100, "SAW-10", "Cut-off Saw", 10, "CC-1000", "Sawing", 42m, 1, "one-shift", true, "stamp-wc-100"),
                new WorkCenterDto(200, "CNC-200", "CNC Turning Center", 20, "CC-2000", "Machining", 78m, 1, "two-shift", true, "stamp-wc-200")
            ],
            Plans =
            [
                new WorkPlanDto(
                    1000, "WP-1001", "SHAFT-08-114", "Drive shaft", "B", 1, 100,
                    [
                        new OperationDto(10, "Cut to length", 100, "Cut-off Saw", 10m, 0.8m, null),
                        new OperationDto(20, "Turn", 200, "CNC Turning Center", 35m, 4.2m, null)
                    ],
                    new DateTime(2026, 1, 14, 9, 30, 0, DateTimeKind.Utc),
                    new DateTime(2026, 3, 2, 13, 5, 0, DateTimeKind.Utc),
                    "stamp-wp-1000")
            ],
            Orders =
            [
                new ProductionOrderDto(
                    5000, "PO-1001", 1000, "WP-1001", 60,
                    new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Unspecified),
                    new DateTime(2026, 6, 8, 6, 0, 0, DateTimeKind.Unspecified),
                    3, 1, "B", snapshot,
                    new DateTime(2026, 5, 29, 9, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 5, 29, 9, 0, 0, DateTimeKind.Utc),
                    "stamp-po-5000")
            ]
        };
    }

    private static string Snapshot(params RoutingSnapshotOperation[] operations) =>
        new RoutingSnapshot("WP-1001", "SHAFT-08-114", "Drive shaft", "B", operations).Serialize();

    private static ApiClient Client(PlantPayload plant) =>
        new(new HttpClient(new PlantHandler(plant)) { BaseAddress = Api });

    /// <summary>What the four master-data routes answer with.</summary>
    private sealed class PlantPayload
    {
        public List<CostCenterDto> CostCenters { get; init; } = [];
        public List<WorkCenterDto> WorkCenters { get; init; } = [];
        public List<WorkPlanDto> Plans { get; init; } = [];
        public List<ProductionOrderDto> Orders { get; init; } = [];
        public PlantSettingsDto Settings { get; init; } =
            new("NW", false, false, false, false, false, 0, 11, new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>Answers each master-data route from the payload; anything else is a 404.</summary>
    private sealed class PlantHandler : HttpMessageHandler
    {
        private readonly PlantPayload _plant;

        public PlantHandler(PlantPayload plant) => _plant = plant;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/api/cost-centers" => Json(_plant.CostCenters),
                "/api/work-centers" => Json(_plant.WorkCenters),
                "/api/work-centers/absences" => Json(Array.Empty<WorkCenterAbsenceDto>()),
                "/api/work-plans" => Json(_plant.Plans),
                "/api/production-orders" => Json(_plant.Orders),
                "/api/plant-settings" => Json(_plant.Settings),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });

        private static HttpResponseMessage Json<T>(T body) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    /// <summary>A server that is simply not there.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }
}
