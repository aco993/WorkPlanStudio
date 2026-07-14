using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Api.Tests;

public sealed class ApiSecurityTests : IAsyncLifetime
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"workplan-api-{Guid.NewGuid():N}.db");
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
    public async Task Protected_endpoint_rejects_anonymous_requests()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/work-centers", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_separate_process_liveness_from_database_readiness()
    {
        using var client = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/health/live", cancellationToken)).StatusCode);
        var readiness = await client.GetFromJsonAsync<ReadinessStatus>("/api/health/ready", cancellationToken);

        Assert.Equal("ready", readiness!.Status);
        Assert.True(readiness.MigrationsApplied);
    }

    [Fact]
    public async Task Registration_login_antiforgery_and_owner_isolation_work_together()
    {
        using var first = _factory.CreateClient();
        using var second = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await RegisterAndLoginAsync(first, $"first-{Guid.NewGuid():N}@example.com", cancellationToken);
        await RegisterAndLoginAsync(second, $"second-{Guid.NewGuid():N}@example.com", cancellationToken);

        var token = await TokenAsync(first, cancellationToken);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/work-centers")
        {
            Content = JsonContent.Create(new WorkCenterDto(0, "WC-1", "Private center", "C1", 100, 1, "UTC", true, 0))
        };
        create.Headers.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.Created, (await first.SendAsync(create, cancellationToken)).StatusCode);

        var otherTenantCenters = await second.GetFromJsonAsync<List<WorkCenterDto>>("/api/work-centers", cancellationToken);
        Assert.Empty(otherTenantCenters!);
    }

    [Fact]
    public async Task Authenticated_order_can_be_scheduled_to_a_persisted_result()
    {
        using var client = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await RegisterAndLoginAsync(client, $"flow-{Guid.NewGuid():N}@example.com", cancellationToken);

        var center = await PostAsync(client, "/api/work-centers",
            new WorkCenterDto(0, "CUT-1", "Cutting", "MFG", 90, 1, "UTC", true, 0), cancellationToken);
        var createdCenter = await center.Content.ReadFromJsonAsync<WorkCenterDto>(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, center.StatusCode);

        var planRequest = new WorkPlanDto(0, "WP-100", "PART-100", "Bracket", "A",
            WorkPlanStatus.Released, 10, default, default, 0,
            [new OperationDto(0, 10, "Cut", createdCenter!.Id, 5, 1, "STEEL", null)]);
        var plan = await PostAsync(client, "/api/work-plans", planRequest, cancellationToken);
        var createdPlan = await plan.Content.ReadFromJsonAsync<WorkPlanDto>(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);

        var release = DateTime.UtcNow.AddMinutes(1);
        var orderRequest = new ProductionOrderDto(0, "PO-100", createdPlan!.Id, "", 10,
            release, release.AddHours(8), 5, ProductionOrderStatus.Released, "", default, default, 0);
        var order = await PostAsync(client, "/api/production-orders", orderRequest, cancellationToken);
        var createdOrder = await order.Content.ReadFromJsonAsync<ProductionOrderDto>(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);

        var queued = await PostAsync(client, "/api/schedule-runs",
            new CreateScheduleRunRequest([createdOrder!.Id], 2, 50, 20260713), cancellationToken);
        var run = await queued.Content.ReadFromJsonAsync<ScheduleRunDto>(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);

        ScheduleRunDto? current = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            current = await client.GetFromJsonAsync<ScheduleRunDto>($"/api/schedule-runs/{run!.Id}", cancellationToken);
            if (current!.Status is ScheduleRunStatus.Completed or ScheduleRunStatus.Failed)
                break;
            await Task.Delay(50, cancellationToken);
        }

        Assert.Equal(ScheduleRunStatus.Completed, current!.Status);
        Assert.Equal(100, current.ProgressPercent);
        Assert.False(string.IsNullOrWhiteSpace(current.ResultJson));
    }

    [Fact]
    public async Task Mutating_endpoint_requires_antiforgery_token()
    {
        using var client = _factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await RegisterAndLoginAsync(client, $"csrf-{Guid.NewGuid():N}@example.com", cancellationToken);
        var response = await client.PostAsJsonAsync("/api/work-centers",
            new WorkCenterDto(0, "WC-X", "Center", "C", 10, 1, "UTC", true, 0), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task RegisterAndLoginAsync(HttpClient client, string email, CancellationToken cancellationToken)
    {
        const string password = "Valid!Password123456";
        var token = await TokenAsync(client, cancellationToken);
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register") { Content = JsonContent.Create(new AuthRequest(email, password)) };
        register.Headers.Add("X-CSRF-TOKEN", token);
        using var registrationResponse = await client.SendAsync(register, cancellationToken);
        Assert.True(registrationResponse.StatusCode == HttpStatusCode.Created,
            $"Registration returned {registrationResponse.StatusCode}: {await registrationResponse.Content.ReadAsStringAsync(cancellationToken)}");
        token = await TokenAsync(client, cancellationToken);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new AuthRequest(email, password)) };
        login.Headers.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login, cancellationToken)).StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(
        HttpClient client, string uri, T body, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(client, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
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
            catch (IOException) { /* Windows may keep a test-server handle briefly; the OS temp directory owns cleanup. */ }
        }
    }
}
