using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Its own database, so nothing this class does is visible to another.</summary>
public sealed class AuthFixture() : ApiFactory("auth");

/// <summary>
/// Signing in, and what happens when you have not.
/// <para>
/// These are the tests the persona demo could never have: there, authorization
/// was real but authentication was a dropdown. Here a 403 means the server
/// looked at a signed token, read its roles and refused.
/// </para>
/// </summary>
public class AuthTests : IClassFixture<AuthFixture>
{
    private readonly AuthFixture _api;

    public AuthTests(AuthFixture api) => _api = api;

    [Fact]
    public async Task Good_credentials_yield_an_access_token_and_the_account_roles()
    {
        var tokens = await ApiFactory.LoginAsync(_api.CreateClient(), "planner", ApiFactory.Password);

        Assert.NotEmpty(tokens.AccessToken);
        Assert.NotEmpty(tokens.RefreshToken);
        Assert.Equal("planner", tokens.User.UserName);
        Assert.Equal(new[] { WorkspaceRoles.Planner }, tokens.User.Roles.ToArray());
        Assert.True(tokens.ExpiresInSeconds is > 0 and <= 7200, "the access token lifetime should be short");

        // Three parts: the token is signed, not merely encoded.
        Assert.Equal(3, tokens.AccessToken.Split('.').Length);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_as_a_problem_document()
    {
        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest("planner", "not-the-password"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("not-the-password", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_account_is_refused_the_same_way_a_wrong_password_is()
    {
        var client = _api.CreateClient();
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", ApiFactory.Password), Ct);
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("planner", "wrong-password"), Ct);

        // Telling the two apart turns a login form into an account enumerator.
        // The per-request trace id is the one field that legitimately differs.
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(await WithoutTraceIdAsync(wrong), await WithoutTraceIdAsync(unknown));
    }

    [Theory]
    [InlineData("/api/work-centers")]
    [InlineData("/api/work-plans")]
    [InlineData("/api/production-orders")]
    [InlineData("/api/plant-settings")]
    [InlineData("/api/auth/me")]
    public async Task Without_a_token_every_protected_route_answers_401(string route)
    {
        var response = await _api.CreateClient().GetAsync(route, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_forged_token_is_rejected_because_the_signature_does_not_verify()
    {
        var tokens = await ApiFactory.LoginAsync(_api.CreateClient(), "planner", ApiFactory.Password);

        // Same header and payload, one character changed in the signature.
        var parts = tokens.AccessToken.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{(parts[2][0] == 'a' ? 'b' : 'a')}{parts[2][1..]}";

        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_guest_may_read_master_data()
    {
        var client = await _api.SignedInAsync("guest");

        var centers = await client.GetFromJsonAsync<List<WorkCenterDto>>("/api/work-centers", Ct);

        Assert.NotNull(centers);
        Assert.NotEmpty(centers);
    }

    [Fact]
    public async Task A_guest_may_not_write_master_data()
    {
        var client = await _api.SignedInAsync("guest");

        var response = await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("GST-001"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_supervisor_may_manage_orders_but_not_master_data()
    {
        var client = await _api.SignedInAsync("supervisor");

        var masterData = await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("SUP-001"), Ct);
        var orders = await client.GetAsync("/api/production-orders", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, masterData.StatusCode);
        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);
    }

    [Fact]
    public async Task Me_answers_with_the_identity_carried_by_the_token()
    {
        var client = await _api.SignedInAsync("supervisor");

        var me = await client.GetFromJsonAsync<UserInfo>("/api/auth/me", Ct);

        Assert.NotNull(me);
        Assert.Equal("supervisor", me.UserName);
        Assert.Equal(new[] { WorkspaceRoles.Supervisor }, me.Roles.ToArray());
    }

    [Fact]
    public async Task Registration_needs_the_master_data_policy()
    {
        var guest = await _api.SignedInAsync("guest");
        var planner = await _api.SignedInAsync("planner");
        var request = new RegisterRequest("new-colleague", "Another-Password-2026", WorkspaceRoles.Guest);

        var refused = await guest.PostAsJsonAsync("/api/auth/register", request, Ct);
        var created = await planner.PostAsJsonAsync("/api/auth/register", request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Registration_refuses_a_password_Identity_would_not_accept()
    {
        var planner = await _api.SignedInAsync("planner");

        var response = await planner.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest("weak-colleague", "short", WorkspaceRoles.Guest), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Registration_refuses_a_role_that_is_not_one_of_the_three()
    {
        var planner = await _api.SignedInAsync("planner");

        var response = await planner.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest("wrong-role", "Another-Password-2026", "Administrator"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        var response = await _api.CreateClient().GetAsync("/health", Ct);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Liveness_answers_without_a_token_and_readiness_reports_the_database()
    {
        var client = _api.CreateClient();

        var live = await client.GetAsync("/health", Ct);
        var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The problem document with its per-request trace id removed.</summary>
    private static async Task<string> WithoutTraceIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        return System.Text.RegularExpressions.Regex.Replace(
            body,
            "\"traceId\":\"[^\"]*\",?",
            "",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(2));
    }
}
