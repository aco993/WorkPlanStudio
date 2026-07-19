using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Api.Tests;

public sealed class CrudAndStateTests : IAsyncLifetime
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"workplan-crud-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Production", $"Data Source={_database}");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Identity:AllowRegistration", "true");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Production"] = $"Data Source={_database}",
                ["Database:Provider"] = "Sqlite",
                ["Database:ApplyMigrationsOnStartup"] = "true",
                ["Identity:AllowRegistration"] = "true"
            }));
        });
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Work_center_crud_validates_conflicts_and_optimistic_concurrency()
    {
        using var client = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await RegisterAndLoginAsync(client, cancellationToken);

        var invalidZone = await PostAsync(client, "/api/work-centers",
            new WorkCenterDto(0, "WC-ZONE", "Invalid zone", "QA", 10, 1, "Not/AZone", true, 0), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidZone.StatusCode);
        Assert.Equal("invalid_time_zone", (await invalidZone.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var createdResponse = await PostAsync(client, "/api/work-centers",
            new WorkCenterDto(0, "WC-CRUD", "Original", "QA", 10, 1, "UTC", true, 0), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<WorkCenterDto>(cancellationToken))!;
        Assert.Equal(1, created.Version);

        var duplicate = await PostAsync(client, "/api/work-centers",
            new WorkCenterDto(0, "WC-CRUD", "Duplicate", "QA", 10, 1, "UTC", true, 0), cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("code_conflict", (await duplicate.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var staleUpdate = await SendJsonAsync(client, HttpMethod.Put, $"/api/work-centers/{created.Id}",
            created with { Name = "Stale", Version = 0 }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
        Assert.Equal("concurrency_conflict", (await staleUpdate.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var update = await SendJsonAsync(client, HttpMethod.Put, $"/api/work-centers/{created.Id}",
            created with { Name = "Updated", HourlyRate = 42, Version = created.Version }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<WorkCenterDto>(cancellationToken))!;
        Assert.Equal("Updated", updated.Name);
        Assert.Equal(2, updated.Version);

        var delete = await DeleteAsync(client, $"/api/work-centers/{updated.Id}", updated.Version, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/work-centers/{updated.Id}", cancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Released_routing_and_order_lifecycle_enforce_immutability_and_delete_guards()
    {
        using var client = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await RegisterAndLoginAsync(client, cancellationToken);
        var center = await CreateCenterAsync(client, "WC-LIFE", cancellationToken);
        var plan = await CreatePlanAsync(client, center.Id, "WP-LIFE", cancellationToken);

        var duplicatePlan = await PostAsync(client, "/api/work-plans", plan with { Id = 0, Version = 0 }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicatePlan.StatusCode);
        Assert.Equal("plan_number_conflict", (await duplicatePlan.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var order = await CreateOrderAsync(client, plan, "PO-LIFE", cancellationToken);

        var immutableOrder = await SendJsonAsync(client, HttpMethod.Put, $"/api/production-orders/{order.Id}",
            order with { Quantity = order.Quantity + 1 }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, immutableOrder.StatusCode);
        Assert.Equal("released_order_immutable", (await immutableOrder.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var immutablePlan = await SendJsonAsync(client, HttpMethod.Put, $"/api/work-plans/{plan.Id}",
            plan with { PartName = "Changed after release" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, immutablePlan.StatusCode);
        Assert.Equal("routing_in_use", (await immutablePlan.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var planDelete = await DeleteAsync(client, $"/api/work-plans/{plan.Id}", plan.Version, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, planDelete.StatusCode);
        Assert.Equal("routing_in_use", (await planDelete.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var orderDelete = await DeleteAsync(client, $"/api/production-orders/{order.Id}", order.Version, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, orderDelete.StatusCode);
        Assert.Equal("order_not_deletable", (await orderDelete.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var cancelled = await SendJsonAsync(client, HttpMethod.Put, $"/api/production-orders/{order.Id}",
            order with { Status = ProductionOrderStatus.Cancelled }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var cancelledOrder = (await cancelled.Content.ReadFromJsonAsync<ProductionOrderDto>(cancellationToken))!;

        Assert.Equal(HttpStatusCode.NoContent,
            (await DeleteAsync(client, $"/api/production-orders/{order.Id}", cancelledOrder.Version, cancellationToken)).StatusCode);
        var currentPlan = await client.GetFromJsonAsync<WorkPlanDto>($"/api/work-plans/{plan.Id}", cancellationToken);
        Assert.NotNull(currentPlan);
        Assert.Equal(plan.Version, currentPlan.Version);
        Assert.Equal(HttpStatusCode.NoContent,
            (await DeleteAsync(client, $"/api/work-plans/{plan.Id}", plan.Version, cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await DeleteAsync(client, $"/api/work-centers/{center.Id}", center.Version, cancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Scheduler_and_assistant_reject_invalid_inputs_and_finished_run_cancellation()
    {
        using var client = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await RegisterAndLoginAsync(client, cancellationToken);
        var center = await CreateCenterAsync(client, "WC-SCHED", cancellationToken);
        var plan = await CreatePlanAsync(client, center.Id, "WP-SCHED", cancellationToken);
        var order = await CreateOrderAsync(client, plan, "PO-SCHED", cancellationToken);

        var emptySelection = await PostAsync(client, "/api/schedule-runs",
            new CreateScheduleRunRequest([]), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptySelection.StatusCode);
        Assert.Equal("invalid_order_count", (await emptySelection.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var invalidParameters = await PostAsync(client, "/api/schedule-runs",
            new CreateScheduleRunRequest([order.Id], MultiStartRuns: 0), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidParameters.StatusCode);
        Assert.Equal("invalid_scheduling_parameters", (await invalidParameters.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var missingOrder = await PostAsync(client, "/api/schedule-runs",
            new CreateScheduleRunRequest([int.MaxValue]), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingOrder.StatusCode);
        Assert.Equal("orders_not_schedulable", (await missingOrder.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var queued = await PostAsync(client, "/api/schedule-runs",
            new CreateScheduleRunRequest([order.Id], MultiStartRuns: 1, LocalSearchMaxSteps: 1, ExactDispatchOrder: true), cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var run = (await queued.Content.ReadFromJsonAsync<ScheduleRunDto>(cancellationToken))!;
        ScheduleRunDto? current = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            current = await client.GetFromJsonAsync<ScheduleRunDto>($"/api/schedule-runs/{run.Id}", cancellationToken);
            if (current!.Status is ScheduleRunStatus.Completed or ScheduleRunStatus.Failed)
                break;
            await Task.Delay(50, cancellationToken);
        }
        Assert.Equal(ScheduleRunStatus.Completed, current!.Status);
        var cancelFinished = await PostAsync<object?>(client, $"/api/schedule-runs/{run.Id}/cancel", null, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, cancelFinished.StatusCode);
        Assert.Equal("schedule_run_finished", (await cancelFinished.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var assistantStatus = await client.GetFromJsonAsync<AssistantStatusDto>("/api/assistant/status", cancellationToken);
        Assert.False(assistantStatus!.Enabled);
        var narration = await PostAsync(client, "/api/assistant/narrate",
            new AssistantNarrationRequest("facts", "en"), cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, narration.StatusCode);
    }

    [Fact]
    public async Task Validation_boundaries_reject_invalid_center_plan_and_order_payloads()
    {
        using var client = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await RegisterAndLoginAsync(client, cancellationToken);

        var invalidCenter = await PostAsync(client, "/api/work-centers",
            new WorkCenterDto(0, "", "", "", -1, 0, "UTC", true, 0), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCenter.StatusCode);
        Assert.Equal("validation_failed", (await invalidCenter.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var invalidPlan = await PostAsync(client, "/api/work-plans",
            new WorkPlanDto(0, "", "", "", null, (WorkPlanStatus)999, 0,
                default, default, 0, []), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPlan.StatusCode);
        Assert.Equal("validation_failed", (await invalidPlan.Content.ReadFromJsonAsync<ApiError>(cancellationToken))!.Code);

        var release = DateTime.UtcNow.AddHours(1);
        foreach (var invalidOrder in new[]
        {
            new ProductionOrderDto(0, "", 1, "", 10, release, release.AddHours(1), 5, ProductionOrderStatus.Draft, "", default, default, 0),
            new ProductionOrderDto(0, "BOUNDARY-QTY", 1, "", 0, release, release.AddHours(1), 5, ProductionOrderStatus.Draft, "", default, default, 0),
            new ProductionOrderDto(0, "BOUNDARY-PRIORITY", 1, "", 10, release, release.AddHours(1), 11, ProductionOrderStatus.Draft, "", default, default, 0),
            new ProductionOrderDto(0, "BOUNDARY-DATE", 1, "", 10, release, release, 5, ProductionOrderStatus.Draft, "", default, default, 0),
            new ProductionOrderDto(0, "BOUNDARY-STATUS", 1, "", 10, release, release.AddHours(1), 5, (ProductionOrderStatus)999, "", default, default, 0)
        })
        {
            var invalidResponse = await PostAsync(client, "/api/production-orders", invalidOrder, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            var error = await invalidResponse.Content.ReadFromJsonAsync<ApiError>(cancellationToken);
            Assert.NotNull(error);
            Assert.StartsWith("invalid_", error.Code);
        }
    }

    private async Task<WorkCenterDto> CreateCenterAsync(HttpClient client, string code, CancellationToken cancellationToken)
    {
        var response = await PostAsync(client, "/api/work-centers",
            new WorkCenterDto(0, code, $"{code} center", "QA", 50, 1, "UTC", true, 0), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkCenterDto>(cancellationToken))!;
    }

    private async Task<WorkPlanDto> CreatePlanAsync(
        HttpClient client, int centerId, string planNumber, CancellationToken cancellationToken)
    {
        var response = await PostAsync(client, "/api/work-plans", new WorkPlanDto(
            0, planNumber, "PART", "Lifecycle part", "A", WorkPlanStatus.Released, 10,
            default, default, 0,
            [new OperationDto(0, 10, "Operation", centerId, 1, 1, "DEFAULT", null)]), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkPlanDto>(cancellationToken))!;
    }

    private async Task<ProductionOrderDto> CreateOrderAsync(
        HttpClient client, WorkPlanDto plan, string orderNumber, CancellationToken cancellationToken)
    {
        var release = DateTime.UtcNow.AddMinutes(1);
        var response = await PostAsync(client, "/api/production-orders", new ProductionOrderDto(
            0, orderNumber, plan.Id, "", 10, release, release.AddHours(8), 5,
            ProductionOrderStatus.Released, "", default, default, 0), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductionOrderDto>(cancellationToken))!;
    }

    private static async Task RegisterAndLoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var email = $"crud-{Guid.NewGuid():N}@example.com";
        const string password = "Valid!Password123456";
        var token = await TokenAsync(client, cancellationToken);
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new AuthRequest(email, password))
        };
        register.Headers.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(register, cancellationToken)).StatusCode);

        token = await TokenAsync(client, cancellationToken);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new AuthRequest(email, password))
        };
        login.Headers.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login, cancellationToken)).StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(
        HttpClient client, string uri, T body, CancellationToken cancellationToken)
    {
        return await SendJsonAsync(client, HttpMethod.Post, uri, body, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client, HttpMethod method, string uri, T body, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, string uri, long version, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{uri}?version={version}");
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<string> TokenAsync(HttpClient client, CancellationToken cancellationToken) =>
        (await client.GetFromJsonAsync<AntiforgeryToken>("/api/auth/antiforgery", cancellationToken))!.Token;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _database, _database + "-shm", _database + "-wal" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }
    }
}
