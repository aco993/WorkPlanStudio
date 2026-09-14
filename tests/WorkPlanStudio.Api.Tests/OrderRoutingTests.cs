using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Its own database: these tests release and cancel orders.</summary>
public sealed class OrderRoutingFixture() : ApiFactory("orderrouting");

/// <summary>
/// What release actually freezes, and what stops moving once it has.
/// <para>
/// A released order's routing is a JSON blob, so the work-centre ids inside it
/// are invisible to SQL. Until they were lifted out, nothing stopped a planner
/// retiring a machine a live shop-floor order depended on: the order simply
/// vanished from the next schedule with a preparation error to show for it.
/// These tests are about that hole being closed on the server too, and about the
/// planning dates staying the wall-clock readings they claim to be.
/// </para>
/// </summary>
public class OrderRoutingTests : IClassFixture<OrderRoutingFixture>
{
    private readonly OrderRoutingFixture _api;

    public OrderRoutingTests(OrderRoutingFixture api) => _api = api;

    [Fact]
    public async Task The_planning_dates_come_back_as_the_wall_clock_reading_that_was_sent()
    {
        var client = await _api.SignedInAsync("planner");
        var (_, plan) = await PlantAsync(client, "OR-200", "OR-WP-1");

        var release = new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Unspecified);
        var created = await Created<ProductionOrderDto>(await client.PostAsJsonAsync(
            "/api/production-orders",
            TestData.NewOrder("OR-PO-1", plan.Id) with { ReleaseLocal = release, DueLocal = release.AddDays(7) },
            Ct));

        // 06:00 on the shop floor, read back as 06:00. An offset applied anywhere
        // between the page and the column would move the first shift of every
        // order twice a year, and only for the half of the year that has one.
        Assert.Equal(release, created.ReleaseLocal);
        Assert.Equal(release.AddDays(7), created.DueLocal);

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var stored = await db.ProductionOrders.AsNoTracking().FirstAsync(o => o.Id == created.Id, Ct);
        Assert.Equal(release, stored.ReleaseLocal);
    }

    [Fact]
    public async Task A_date_sent_with_a_time_zone_keeps_its_reading_rather_than_being_converted()
    {
        var client = await _api.SignedInAsync("planner");
        var (_, plan) = await PlantAsync(client, "OR-201", "OR-WP-2");

        var created = await Created<ProductionOrderDto>(await client.PostAsJsonAsync(
            "/api/production-orders",
            TestData.NewOrder("OR-PO-2", plan.Id) with
            {
                ReleaseLocal = new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc),
                DueLocal = new DateTime(2026, 6, 8, 6, 0, 0, DateTimeKind.Utc)
            },
            Ct));

        // The claim is dropped, the reading is kept. Converting it would need a
        // zone the model does not have, and honouring it would let one order
        // carry two different worlds.
        Assert.Equal(new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Unspecified), created.ReleaseLocal);
        Assert.Equal(DateTimeKind.Unspecified, created.ReleaseLocal.Kind);
    }

    [Fact]
    public async Task Releasing_an_order_records_the_work_centres_its_frozen_routing_needs()
    {
        var client = await _api.SignedInAsync("planner");
        var (center, plan) = await PlantAsync(client, "OR-202", "OR-WP-3");
        var order = await Created<ProductionOrderDto>(
            await client.PostAsJsonAsync("/api/production-orders", TestData.NewOrder("OR-PO-3", plan.Id), Ct));

        Assert.Empty(await RoutingCentersAsync(order.Id));

        var released = await client.PostAsJsonAsync($"/api/production-orders/{order.Id}/release", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        Assert.Equal([center.Id], await RoutingCentersAsync(order.Id));
    }

    [Fact]
    public async Task A_work_centre_a_released_order_needs_can_be_neither_retired_nor_deleted()
    {
        var client = await _api.SignedInAsync("planner");
        var (center, plan) = await PlantAsync(client, "OR-203", "OR-WP-4");
        var order = await Created<ProductionOrderDto>(
            await client.PostAsJsonAsync("/api/production-orders", TestData.NewOrder("OR-PO-4", plan.Id), Ct));
        await client.PostAsJsonAsync($"/api/production-orders/{order.Id}/release", new { }, Ct);

        // Take the plan out of the way, so only the order's frozen routing still
        // names the machine. This is the route around the old guard: set the plan
        // back to Draft and the work centre looked free.
        var current = await client.GetFromJsonAsync<WorkPlanDto>($"/api/work-plans/{plan.Id}", Ct);
        var draft = await client.PutAsJsonAsync($"/api/work-plans/{plan.Id}", current! with { Status = 0 }, Ct);
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        var stored = await client.GetFromJsonAsync<WorkCenterDto>($"/api/work-centers/{center.Id}", Ct);
        var retire = await client.PutAsJsonAsync(
            $"/api/work-centers/{center.Id}", stored! with { IsActive = false }, Ct);
        var delete = await client.DeleteAsync($"/api/work-centers/{center.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, retire.StatusCode);
        Assert.Contains("Val_WorkCenterOrderUse", await retire.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_order_ends_the_dependency_but_keeps_the_snapshot()
    {
        var client = await _api.SignedInAsync("planner");
        var (center, plan) = await PlantAsync(client, "OR-204", "OR-WP-5");
        var order = await Created<ProductionOrderDto>(
            await client.PostAsJsonAsync("/api/production-orders", TestData.NewOrder("OR-PO-5", plan.Id), Ct));
        await client.PostAsJsonAsync($"/api/production-orders/{order.Id}/release", new { }, Ct);

        // Take the plan out of the way, so the order's routing index is the only
        // thing left holding the machine.
        var current = await client.GetFromJsonAsync<WorkPlanDto>($"/api/work-plans/{plan.Id}", Ct);
        await client.PutAsJsonAsync($"/api/work-plans/{plan.Id}", current! with { Status = 0 }, Ct);

        var cancelled = await client.PostAsJsonAsync($"/api/production-orders/{order.Id}/cancel", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var body = await cancelled.Content.ReadFromJsonAsync<ProductionOrderDto>(Ct);
        Assert.NotEmpty(body!.RoutingSnapshotJson);
        Assert.Empty(await RoutingCentersAsync(order.Id));

        var stored = await client.GetFromJsonAsync<WorkCenterDto>($"/api/work-centers/{center.Id}", Ct);
        var retire = await client.PutAsJsonAsync(
            $"/api/work-centers/{center.Id}", stored! with { IsActive = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
    }

    /// <summary>A work centre and a released plan with one operation on it.</summary>
    private async Task<(WorkCenterDto Center, WorkPlanDto Plan)> PlantAsync(
        HttpClient client, string centerCode, string planNumber)
    {
        var center = await Created<WorkCenterDto>(
            await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter(centerCode), Ct));
        var plan = await Created<WorkPlanDto>(
            await client.PostAsJsonAsync("/api/work-plans", TestData.NewPlan(planNumber, center.Id), Ct));
        return (center, plan);
    }

    private async Task<int[]> RoutingCentersAsync(int orderId)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        return await db.OrderRoutingCenters
            .AsNoTracking()
            .Where(r => r.ProductionOrderId == orderId)
            .Select(r => r.WorkCenterId)
            .OrderBy(id => id)
            .ToArrayAsync(Ct);
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Ct)
               ?? throw new InvalidOperationException("The create response was empty.");
    }
}
