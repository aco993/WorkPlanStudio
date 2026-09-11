using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Remote;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Connected mode, from the client's side.
/// <para>
/// The property that matters most here is the one about <em>not</em> changing:
/// with no API configured the app must register and render exactly what it did
/// before, because that is the build published to a static host. The rest is
/// the swap itself — a real identity provider in place of the persona — and the
/// one piece of plumbing that is easy to get subtly wrong, renewing an expired
/// token without spending two refresh tokens on it.
/// </para>
/// </summary>
public sealed class RemoteAuthTests : AppBunitContext
{
    private static readonly Uri Api = new("https://api.example.invalid/");

    // ----- registration: offline stays offline -----

    [Fact]
    public void With_nothing_configured_the_app_registers_no_remote_services()
    {
        var services = Offline();

        var options = services.AddOptionalApi(Configuration());

        Assert.False(options.IsConnected);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<RemoteAuthenticationStateProvider>());
        Assert.IsType<DemoAuthenticationStateProvider>(provider.GetRequiredService<AuthenticationStateProvider>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("/relative/only")]
    [InlineData("ftp://files.example.invalid")]
    public void An_address_that_is_not_an_absolute_web_url_leaves_the_app_offline(string configured)
    {
        // A typo in a deployment's settings file should cost the persona demo
        // nothing; the alternative is a blank page on a static host.
        var options = new ApiOptions(configured);

        Assert.False(options.IsConnected);
        Assert.Null(options.Address);
    }

    // ----- registration: a configured API swaps the identity and the scheduler -----

    [Fact]
    public void A_configured_api_replaces_the_persona_provider_and_the_scheduler()
    {
        var services = Offline();

        var options = services.AddOptionalApi(Configuration(Api.ToString()));

        Assert.True(options.IsConnected);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<RemoteAuthenticationStateProvider>(
            scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>());
        Assert.IsType<RemoteScheduleService>(
            scope.ServiceProvider.GetRequiredService<IProductionScheduleService>());
    }

    [Fact]
    public async Task The_roles_in_a_token_are_the_roles_the_policies_read()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options => options.AddWorkspacePolicies());
        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var guest = RemoteAuthenticationStateProvider.PrincipalFor(
            new UserInfo("someone", [WorkspaceRoles.Guest]));
        var planner = RemoteAuthenticationStateProvider.PrincipalFor(
            new UserInfo("someone-else", [WorkspaceRoles.Planner]));

        // Same policy objects, same table, a different source of identity.
        Assert.False((await authorization.AuthorizeAsync(guest, null, Permissions.ManageMasterData)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(planner, null, Permissions.ManageMasterData)).Succeeded);
        Assert.Equal(
            RemoteAuthenticationStateProvider.AuthenticationType,
            planner.Identity?.AuthenticationType);
    }

    // ----- the top bar -----

    [Fact]
    public void The_persona_switcher_is_shown_when_no_api_is_configured()
    {
        ArrangeMenu(ApiOptions.Offline);

        var cut = Render<AccountMenu>();

        Assert.NotEmpty(cut.FindAll(".persona-summary"));
        Assert.Empty(cut.FindAll("a[href='sign-in']"));
    }

    [Fact]
    public void The_persona_switcher_is_gone_once_an_api_is_configured()
    {
        ArrangeMenu(new ApiOptions(Api.ToString()));

        var cut = Render<AccountMenu>();

        // Roles come from a signed token in this mode, so a control that offers
        // to change them would be offering something the server will not honour.
        Assert.Empty(cut.FindAll(".persona-summary"));
        Assert.NotEmpty(cut.FindAll("a[href='sign-in']"));
    }

    [Fact]
    public async Task A_signed_in_account_replaces_the_sign_in_link_with_its_own_name()
    {
        var tokens = ArrangeMenu(new ApiOptions(Api.ToString()));
        await tokens.AcceptAsync(
            new AuthTokens("access", 600, "refresh", new UserInfo("m.planner", [WorkspaceRoles.Planner])),
            Xunit.TestContext.Current.CancellationToken);

        var cut = Render<AccountMenu>();

        Assert.Contains("m.planner", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".persona-summary"));
        Assert.NotEmpty(cut.FindAll(".persona-avatar.role-planner"));
    }

    // ----- renewing a token -----

    [Fact]
    public async Task A_401_renews_the_token_once_and_retries_the_request()
    {
        var api = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var auth = new ScriptedHandler(_ => Json(Tokens("access-2", "refresh-2")));

        var store = await SignedInStore(auth);
        using var client = new HttpClient(new BearerTokenHandler(store, api)) { BaseAddress = Api };

        var response = await client.GetAsync("api/work-centers", Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, api.Calls.Count);
        Assert.Equal("access-1", api.Calls[0].Headers.Authorization?.Parameter);
        Assert.Equal("access-2", api.Calls[1].Headers.Authorization?.Parameter);
        Assert.Single(auth.Calls);
    }

    [Fact]
    public async Task A_401_that_survives_the_renewal_ends_the_session()
    {
        var api = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var auth = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var store = await SignedInStore(auth);
        using var client = new HttpClient(new BearerTokenHandler(store, api)) { BaseAddress = Api };

        var response = await client.GetAsync("api/work-centers", Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(api.Calls);          // one attempt, no retry loop
        Assert.Null(store.User);           // and the client no longer claims a session
    }

    [Fact]
    public async Task An_expired_token_is_renewed_once_however_many_requests_are_waiting()
    {
        var auth = new ScriptedHandler(_ => Json(Tokens("access-2", "refresh-2")));
        var clock = new AdjustableClock(DateTimeOffset.UnixEpoch);
        var store = new TokenStore(new AuthApi(new HttpClient(auth) { BaseAddress = Api }), new MemoryStorage(), clock);
        await store.AcceptAsync(Tokens("access-1", "refresh-1"), Xunit.TestContext.Current.CancellationToken);

        clock.Now = clock.Now.AddSeconds(600);   // the access token has expired

        var waiting = Enumerable.Range(0, 8).Select(_ => store.GetAccessTokenAsync().AsTask()).ToArray();
        var tokens = await Task.WhenAll(waiting);

        // Rotation retires a refresh token on first use, so a second concurrent
        // exchange would present a spent one - which the server reads as theft
        // and answers by ending every session. The lock is what prevents that.
        Assert.Single(auth.Calls);
        Assert.All(tokens, token => Assert.Equal("access-2", token));
    }

    [Fact]
    public async Task Signing_out_forgets_the_session_and_the_stored_token()
    {
        var auth = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var storage = new MemoryStorage();
        var store = new TokenStore(new AuthApi(new HttpClient(auth) { BaseAddress = Api }), storage, TimeProvider.System);
        await store.AcceptAsync(Tokens("access-1", "refresh-1"), Xunit.TestContext.Current.CancellationToken);

        await store.SignOutAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Null(store.User);
        Assert.Null(await storage.ReadAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Single(auth.Calls);
        Assert.Equal("access-1", auth.Calls[0].Headers.Authorization?.Parameter);
    }

    // ----- a server run becomes the page's view model -----

    [Fact]
    public void A_server_schedule_is_rebuilt_into_the_view_model_the_page_renders()
    {
        var response = new ScheduleRunResponse(
            HasData: true,
            Kpis: new ScheduleKpisDto(1200, 1.0, 0, 0.8, 0, 2),
            Rows:
            [
                new GanttRowDto(
                    "SAW-10 — Cut-off Saw",
                    [new GanttBarDto(1, "PO-1", 0, 1, 0, 600, false, 0, 60)],
                    [new GanttClosedSegmentDto(600, 900, (int)WorkPlanStudio.WorkingTime.SegmentKind.Break, "lunch")])
            ],
            Jobs: [new JobRowDto(1, "PO-1", "Drive shaft", 0, 5000, 600, -4400, false)],
            MakespanSeconds: 1200,
            MinutesPerWorkingDay: 480,
            LocalSearchSteps: 3,
            HorizonUtc: new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc),
            TotalPausedSeconds: 120,
            UtilizationByWorkCenter: new Dictionary<string, double> { ["SAW-10 — Cut-off Saw"] = 0.8 },
            EquivalentRules: [(int)DispatchRule.Fifo],
            PreparationErrors:
            [
                new SchedulePreparationIssueDto(7, "PO-7", 20, (int)SchedulePreparationErrorCode.InactiveWorkCenter, "GRD-400")
            ],
            Explanation: null,
            Signature: "1:1@1#0[0-600];");

        var result = RemoteMapping.ToResult(response);

        Assert.True(result.HasData);
        Assert.Equal(1200, result.MakespanSeconds);
        Assert.Equal(60, result.Rows[0].Bars[0].SetupSeconds);
        Assert.Equal(WorkPlanStudio.WorkingTime.SegmentKind.Break, result.Rows[0].Closed[0].Kind);
        Assert.Equal(DispatchRule.Fifo, Assert.Single(result.EquivalentRules));
        Assert.Equal(SchedulePreparationErrorCode.InactiveWorkCenter, Assert.Single(result.PreparationErrors).Code);
        Assert.Equal(new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc), result.Horizon);
    }

    [Fact]
    public void The_engine_parameters_survive_the_round_trip_to_the_wire()
    {
        var parameters = new SchedulingParameters
        {
            DispatchRule = DispatchRule.WeightedShortestProcessingTime,
            DueDateRule = DueDateRule.EqualSlack,
            Seed = 4242,
            MultiStartRuns = 3,
            LocalSearchMaxSteps = 17,
            MinutesPerWorkingDay = 600
        };

        var request = RemoteMapping.ToRequest(parameters);

        Assert.Equal((int)DispatchRule.WeightedShortestProcessingTime, request.DispatchRule);
        Assert.Equal((int)DueDateRule.EqualSlack, request.DueDateRule);
        Assert.Equal(4242, request.Seed);
        Assert.Equal(3, request.MultiStartRuns);
        Assert.Equal(17, request.LocalSearchMaxSteps);
        Assert.Equal(600, request.MinutesPerWorkingDay);
    }

    // ----- support -----

    /// <summary>The services Program.cs registers before the optional API is offered one.</summary>
    private static ServiceCollection Offline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, SilentJsRuntime>();
        services.AddAuthorizationCore(options => options.AddWorkspacePolicies());
        services.AddScoped<IPersonaStore, FakePersonaStore>();
        services.AddScoped<DemoAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(
            provider => provider.GetRequiredService<DemoAuthenticationStateProvider>());
        services.AddScoped<IProductionScheduleService, FakeScheduleService>();
        return services;
    }

    private static IConfiguration Configuration(string? baseAddress = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:BaseAddress"] = baseAddress })
            .Build();

    /// <summary>Registers what <see cref="AccountMenu"/> needs and returns the session it will read.</summary>
    private TokenStore ArrangeMenu(ApiOptions options)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(options);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);

        var store = new TokenStore(
            new AuthApi(new HttpClient(new ScriptedHandler()) { BaseAddress = Api }),
            new MemoryStorage(),
            TimeProvider.System);
        Services.AddSingleton(store);
        Services.AddSingleton(provider => new RemoteAuthenticationStateProvider(
            new AuthApi(new HttpClient(new ScriptedHandler()) { BaseAddress = Api }),
            provider.GetRequiredService<TokenStore>()));

        return store;
    }

    private static AuthTokens Tokens(string access, string refresh) =>
        new(access, 600, refresh, new UserInfo("someone", [WorkspaceRoles.Planner]));

    private static HttpResponseMessage Json(AuthTokens tokens) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(tokens) };

    private static async Task<TokenStore> SignedInStore(HttpMessageHandler auth)
    {
        var store = new TokenStore(
            new AuthApi(new HttpClient(auth) { BaseAddress = Api }), new MemoryStorage(), TimeProvider.System);
        await store.AcceptAsync(Tokens("access-1", "refresh-1"), Xunit.TestContext.Current.CancellationToken);
        return store;
    }

    /// <summary>Answers a fixed sequence of responses and keeps every request for inspection.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _script;
        private int _next;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) => _script = script;

        public List<HttpRequestMessage> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add(request);

            var index = Math.Min(Interlocked.Increment(ref _next) - 1, _script.Length - 1);
            return Task.FromResult(_script.Length == 0
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : _script[index](request));
        }
    }

    /// <summary>The refresh-token store without a browser.</summary>
    private sealed class MemoryStorage : IRefreshTokenStorage
    {
        private string? _token;

        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_token);

        public ValueTask WriteAsync(string token, CancellationToken cancellationToken = default)
        {
            _token = token;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            _token = null;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Browser storage is not reachable from a plain service collection; nothing here calls into it.</summary>
    private sealed class SilentJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);
    }

    /// <summary>A clock the test moves, so token expiry needs no waiting.</summary>
    private sealed class AdjustableClock : TimeProvider
    {
        public AdjustableClock(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
