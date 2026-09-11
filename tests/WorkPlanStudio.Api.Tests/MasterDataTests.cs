using System.Net;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Its own database: these tests write.</summary>
public sealed class MasterDataFixture() : ApiFactory("masterdata");

/// <summary>
/// The CRUD routes, their refusals, and the race between two writers.
/// <para>
/// The refusals matter more than the happy paths. An API that accepts a work
/// centre with a blank code and a negative rate is not a data store, it is a
/// funnel, and the failure surfaces months later as a schedule nobody can
/// explain.
/// </para>
/// </summary>
public class MasterDataTests : IClassFixture<MasterDataFixture>
{
    private readonly MasterDataFixture _api;

    public MasterDataTests(MasterDataFixture api) => _api = api;

    [Fact]
    public async Task A_work_center_can_be_created_read_updated_and_deleted()
    {
        var client = await _api.SignedInAsync("planner");

        var created = await Created<WorkCenterDto>(await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-100"), Ct));
        Assert.NotEqual(0, created.Id);
        Assert.NotEmpty(created.ConcurrencyStamp);

        var read = await client.GetFromJsonAsync<WorkCenterDto>($"/api/work-centers/{created.Id}", Ct);
        Assert.Equal(created, read);

        var updated = await client.PutAsJsonAsync(
            $"/api/work-centers/{created.Id}", created with { Name = "Renamed", HourlyRate = 61.5m }, Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var after = await updated.Content.ReadFromJsonAsync<WorkCenterDto>(Ct);
        Assert.NotNull(after);
        Assert.Equal("Renamed", after.Name);
        Assert.NotEqual(created.ConcurrencyStamp, after.ConcurrencyStamp);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/work-centers/{created.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/work-centers/{created.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_work_center_without_a_code_is_refused_with_the_failing_field()
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.PostAsJsonAsync(
            "/api/work-centers", TestData.NewCenter("MD-101") with { Code = "  ", HourlyRate = -1m }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadProblemAsync(response);
        Assert.Contains("Code", problem.Errors.Keys);
        Assert.Contains("HourlyRate", problem.Errors.Keys);
        Assert.Contains("Val_Required", problem.Errors["Code"]);
    }

    [Fact]
    public async Task A_duplicate_work_center_code_is_a_conflict()
    {
        var client = await _api.SignedInAsync("planner");
        await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-102"), Ct);

        var again = await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-102"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("Val_CodeTaken", await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_code_uniqueness_rule_ignores_case()
    {
        var client = await _api.SignedInAsync("planner");
        await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-103"), Ct);

        var lowercase = await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("md-103"), Ct);

        // The column collates NOCASE; two centres whose codes differ only in case
        // would be indistinguishable on a shop-floor printout.
        Assert.Equal(HttpStatusCode.Conflict, lowercase.StatusCode);
    }

    [Fact]
    public async Task Two_writers_who_read_the_same_row_do_not_both_win()
    {
        var client = await _api.SignedInAsync("planner");
        var center = await Created<WorkCenterDto>(await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-104"), Ct));

        // Both read the same version, as two browser tabs would.
        var first = await client.PutAsJsonAsync($"/api/work-centers/{center.Id}", center with { Name = "First writer" }, Ct);
        var second = await client.PutAsJsonAsync($"/api/work-centers/{center.Id}", center with { Name = "Second writer" }, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var stored = await client.GetFromJsonAsync<WorkCenterDto>($"/api/work-centers/{center.Id}", Ct);
        Assert.Equal("First writer", stored?.Name);
    }

    [Fact]
    public async Task An_update_without_the_version_it_read_is_refused()
    {
        var client = await _api.SignedInAsync("planner");
        var center = await Created<WorkCenterDto>(await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-105"), Ct));

        var response = await client.PutAsJsonAsync(
            $"/api/work-centers/{center.Id}", center with { Name = "No version", ConcurrencyStamp = "" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_work_plan_round_trips_with_its_operations()
    {
        var client = await _api.SignedInAsync("planner");
        var center = await Created<WorkCenterDto>(await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-110"), Ct));

        var created = await Created<WorkPlanDto>(
            await client.PostAsJsonAsync("/api/work-plans", TestData.NewPlan("MD-WP-1", center.Id), Ct));

        Assert.Equal(2, created.Operations.Count);
        Assert.Equal(new[] { 10, 20 }, created.Operations.Select(o => o.OperationNumber).ToArray());

        var shortened = created with { Operations = [created.Operations[0]] };
        var updated = await client.PutAsJsonAsync($"/api/work-plans/{created.Id}", shortened, Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var after = await client.GetFromJsonAsync<WorkPlanDto>($"/api/work-plans/{created.Id}", Ct);
        Assert.NotNull(after);
        Assert.Single(after.Operations);
    }

    [Fact]
    public async Task A_work_plan_whose_operation_names_an_unknown_work_center_is_refused()
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.PostAsJsonAsync("/api/work-plans", TestData.NewPlan("MD-WP-2", workCenterId: 987654), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Val_WorkCenterMissing", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_plan_with_no_operations_is_refused()
    {
        var client = await _api.SignedInAsync("planner");
        var center = await Created<WorkCenterDto>(await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-111"), Ct));

        var response = await client.PostAsJsonAsync(
            "/api/work-plans", TestData.NewPlan("MD-WP-3", center.Id) with { Operations = [] }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Val_NeedOperation", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_center_an_operation_still_uses_cannot_be_deleted()
    {
        var client = await _api.SignedInAsync("planner");
        var center = await Created<WorkCenterDto>(await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-112"), Ct));
        await client.PostAsJsonAsync("/api/work-plans", TestData.NewPlan("MD-WP-4", center.Id), Ct);

        var response = await client.DeleteAsync($"/api/work-centers/{center.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Val_WorkCenterInUse", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releasing_an_order_freezes_the_routing_and_a_later_plan_edit_does_not_change_it()
    {
        var planner = await _api.SignedInAsync("planner");
        var supervisor = await _api.SignedInAsync("supervisor");

        var center = await Created<WorkCenterDto>(await planner.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-120"), Ct));
        var plan = await Created<WorkPlanDto>(await planner.PostAsJsonAsync("/api/work-plans", TestData.NewPlan("MD-WP-5", center.Id), Ct));
        var order = await Created<ProductionOrderDto>(
            await supervisor.PostAsJsonAsync("/api/production-orders", TestData.NewOrder("MD-PO-1", plan.Id), Ct));

        var released = await supervisor.PostAsync($"/api/production-orders/{order.Id}/release", content: null, Ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        var frozen = await released.Content.ReadFromJsonAsync<ProductionOrderDto>(Ct);
        Assert.NotNull(frozen);
        Assert.Equal(1, frozen.Status);
        Assert.Contains("Cut to length", frozen.RoutingSnapshotJson, StringComparison.Ordinal);

        // The plan is rewritten afterwards. The order must not notice.
        var rewritten = plan with
        {
            Operations = [plan.Operations[0] with { Description = "Something else entirely" }]
        };
        Assert.Equal(HttpStatusCode.OK, (await planner.PutAsJsonAsync($"/api/work-plans/{plan.Id}", rewritten, Ct)).StatusCode);

        var reloaded = await supervisor.GetFromJsonAsync<ProductionOrderDto>($"/api/production-orders/{order.Id}", Ct);
        Assert.NotNull(reloaded);
        Assert.Contains("Cut to length", reloaded.RoutingSnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Something else entirely", reloaded.RoutingSnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_released_order_may_not_be_edited_or_deleted()
    {
        var client = await _api.SignedInAsync("supervisor");
        var planner = await _api.SignedInAsync("planner");

        var center = await Created<WorkCenterDto>(await planner.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-121"), Ct));
        var plan = await Created<WorkPlanDto>(await planner.PostAsJsonAsync("/api/work-plans", TestData.NewPlan("MD-WP-6", center.Id), Ct));
        var order = await Created<ProductionOrderDto>(
            await client.PostAsJsonAsync("/api/production-orders", TestData.NewOrder("MD-PO-2", plan.Id), Ct));
        await client.PostAsync($"/api/production-orders/{order.Id}/release", content: null, Ct);

        var edit = await client.PutAsJsonAsync($"/api/production-orders/{order.Id}", order with { Quantity = 99 }, Ct);
        var delete = await client.DeleteAsync($"/api/production-orders/{order.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task An_order_whose_target_date_precedes_its_release_is_refused()
    {
        var client = await _api.SignedInAsync("supervisor");
        var planner = await _api.SignedInAsync("planner");
        var center = await Created<WorkCenterDto>(await planner.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-122"), Ct));
        var plan = await Created<WorkPlanDto>(await planner.PostAsJsonAsync("/api/work-plans", TestData.NewPlan("MD-WP-7", center.Id), Ct));

        var response = await client.PostAsJsonAsync(
            "/api/production-orders",
            TestData.NewOrder("MD-PO-3", plan.Id) with { DueUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Val_DueBeforeRelease", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plant_settings_reject_a_state_that_does_not_exist_and_accept_one_that_does()
    {
        var client = await _api.SignedInAsync("planner");
        var current = await client.GetFromJsonAsync<PlantSettingsDto>("/api/plant-settings", Ct);
        Assert.NotNull(current);

        var nonsense = await client.PutAsJsonAsync("/api/plant-settings", current with { State = "ZZ" }, Ct);
        var real = await client.PutAsJsonAsync("/api/plant-settings", current with { State = "by", MinimumRestHours = 10 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Contains("Val_StateInvalid", await nonsense.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        var saved = await real.Content.ReadFromJsonAsync<PlantSettingsDto>(Ct);
        Assert.Equal("BY", saved?.State);
    }

    [Fact]
    public async Task Only_the_plant_rules_policy_may_change_the_plant_rules()
    {
        var supervisor = await _api.SignedInAsync("supervisor");
        var current = await supervisor.GetFromJsonAsync<PlantSettingsDto>("/api/plant-settings", Ct);

        var response = await supervisor.PutAsJsonAsync("/api/plant-settings", current!, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_absence_needs_the_absence_policy_and_a_work_center_that_exists()
    {
        var supervisor = await _api.SignedInAsync("supervisor");
        var guest = await _api.SignedInAsync("guest");
        var planner = await _api.SignedInAsync("planner");
        var center = await Created<WorkCenterDto>(await planner.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("MD-130"), Ct));

        var absence = new WorkCenterAbsenceDto(
            0, center.Id,
            new DateTime(2026, 6, 3, 7, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 6, 3, 15, 0, 0, DateTimeKind.Unspecified),
            0, "Planned service");

        var refused = await guest.PostAsJsonAsync("/api/work-centers/absences", absence, Ct);
        var accepted = await supervisor.PostAsJsonAsync("/api/work-centers/absences", absence, Ct);
        var backwards = await supervisor.PostAsJsonAsync(
            "/api/work-centers/absences", absence with { End = absence.Start.AddHours(-1) }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    [Fact]
    public async Task A_missing_row_answers_404_with_a_problem_document()
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.GetAsync("/api/work-plans/987654", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Ct)
               ?? throw new InvalidOperationException("The create response was empty.");
    }

    private static async Task<ValidationProblemBody> ReadProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ValidationProblemBody>(Ct)
        ?? throw new InvalidOperationException("The problem document was empty.");

    /// <summary>Just enough of RFC 9457 to assert on: the per-field errors.</summary>
    private sealed record ValidationProblemBody(
        string? Type,
        string? Title,
        int Status,
        Dictionary<string, string[]> Errors);
}
