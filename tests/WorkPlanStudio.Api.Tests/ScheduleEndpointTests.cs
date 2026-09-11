using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Its own database, seeded with the sample plant so there is something to schedule.</summary>
public sealed class ScheduleFixture() : ApiFactory("schedule");

/// <summary>
/// The schedule endpoint, and the property that justifies it existing at all.
/// </summary>
public class ScheduleEndpointTests : IClassFixture<ScheduleFixture>
{
    private readonly ScheduleFixture _api;

    public ScheduleEndpointTests(ScheduleFixture api) => _api = api;

    [Fact]
    public async Task A_run_returns_a_schedule_for_the_released_orders()
    {
        var client = await _api.SignedInAsync("planner");

        var result = await RunAsync(client, new ScheduleRunRequest());

        Assert.True(result.HasData);
        Assert.Equal(3, result.Kpis.JobCount);
        Assert.Empty(result.PreparationErrors);
        Assert.NotEmpty(result.Rows);
        Assert.NotEmpty(result.Jobs);
        Assert.True(result.MakespanSeconds > 0);
        Assert.NotNull(result.Explanation);
        Assert.NotEmpty(result.Signature);
    }

    [Fact]
    public async Task The_server_produces_the_schedule_the_browser_would_have_produced()
    {
        var client = await _api.SignedInAsync("planner");

        // Deliberately none of the engine defaults. If the endpoint dropped the
        // request body on the floor it would schedule with EarliestDueDate and
        // the two signatures would part company.
        var parameters = new ScheduleRunRequest
        {
            DispatchRule = (int)DispatchRule.LongestProcessingTime,
            DueDateRule = (int)DueDateRule.NumberOfOperations,
            Seed = 12345,
            MultiStartRuns = 4,
            LocalSearchMaxSteps = 500
        };

        var overHttp = await RunAsync(client, parameters);
        var inProcess = RunTheEngineDirectly(ScheduleMapping.ToParameters(parameters));

        // The signature is the engine's canonical fingerprint of the placement:
        // every operation, its machine, its slot and its start and end second. If
        // the trip through EF, JSON and HTTP had perturbed one rounding or one
        // ordering anywhere, these two strings would differ.
        Assert.Equal(inProcess.Signature, overHttp.Signature);
        Assert.Equal(inProcess.Makespan, overHttp.MakespanSeconds);
        Assert.Equal(inProcess.Tardiness, overHttp.Kpis.TotalTardinessSeconds);
    }

    [Fact]
    public async Task The_same_request_twice_yields_the_same_schedule()
    {
        var client = await _api.SignedInAsync("planner");
        var parameters = new ScheduleRunRequest { DispatchRule = (int)DispatchRule.ShortestProcessingTime };

        var first = await RunAsync(client, parameters);
        var second = await RunAsync(client, parameters);

        Assert.Equal(first.Signature, second.Signature);
    }

    [Fact]
    public async Task The_explanation_names_the_rule_the_caller_asked_for()
    {
        var client = await _api.SignedInAsync("planner");

        var result = await RunAsync(
            client, new ScheduleRunRequest { DispatchRule = (int)DispatchRule.ShortestProcessingTime });

        Assert.NotNull(result.Explanation);
        Assert.Equal((int)DispatchRule.ShortestProcessingTime, result.Explanation.CurrentRule);
    }

    [Fact]
    public async Task A_guest_may_run_a_schedule()
    {
        var client = await _api.SignedInAsync("guest");

        var response = await client.PostAsJsonAsync("/api/schedule/run", new ScheduleRunRequest(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Running_a_schedule_needs_an_account()
    {
        var response = await _api.CreateClient().PostAsJsonAsync("/api/schedule/run", new ScheduleRunRequest(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(null, 99999, null)]
    [InlineData(null, null, 0)]
    public async Task A_parameter_outside_the_engine_limits_is_a_validation_problem(
        int? multiStartRuns, int? localSearchSteps, int? minutesPerDay)
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.PostAsJsonAsync("/api/schedule/run", new ScheduleRunRequest
        {
            MultiStartRuns = multiStartRuns,
            LocalSearchMaxSteps = localSearchSteps,
            MinutesPerWorkingDay = minutesPerDay
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_empty_body_runs_with_the_engine_defaults()
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.PostAsJsonAsync<ScheduleRunRequest?>("/api/schedule/run", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ScheduleRunResponse>(Ct);
        Assert.Equal(ScheduleResult.DefaultMinutesPerWorkingDay, result?.MinutesPerWorkingDay);
    }

    private static async Task<ScheduleRunResponse> RunAsync(HttpClient client, ScheduleRunRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/schedule/run", request, Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScheduleRunResponse>(Ct)
               ?? throw new InvalidOperationException("The schedule response was empty.");
    }

    /// <summary>
    /// The browser's own path, run here: the same <see cref="ScheduleMapper"/>
    /// source the WebAssembly app compiles (linked into this assembly, see the
    /// API project file), over the same rows, through the same engine.
    /// </summary>
    private (string Signature, long Makespan, long Tardiness) RunTheEngineDirectly(SchedulingParameters parameters)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();

        var orders = db.ProductionOrders
            .AsNoTracking()
            .Where(o => o.Status == ProductionOrderStatus.Released && o.RoutingSnapshotJson != "")
            .OrderBy(o => o.DueLocal)
            .ToList();
        var centers = db.WorkCenters.AsNoTracking().OrderBy(c => c.Code).ToList();
        var absences = db.WorkCenterAbsences.AsNoTracking().OrderBy(a => a.Start).ThenBy(a => a.WorkCenterId).ToList();
        var settings = db.PlantSettings.AsNoTracking().First(s => s.Id == PlantSettings.SingletonId);

        var preparation = ScheduleMapper.BuildInputFromOrders(
            orders, centers, parameters, ShopCalendar.From(settings, centers, absences));
        Assert.NotNull(preparation.Input);

        var result = new SchedulingEngine().Run(preparation.Input.Context);
        return (result.Schedule.Signature(), result.Schedule.MakespanSeconds, result.Evaluation.TotalTardinessSeconds);
    }
}
