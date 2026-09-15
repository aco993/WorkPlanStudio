using System.Net;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Its own database: a locked-out account stays locked out for the rest of the class.</summary>
public sealed class LockoutFixture() : ApiFactory("lockout");

/// <summary>
/// Guessing a password has to get more expensive, not stay free.
/// </summary>
public class LockoutTests : IClassFixture<LockoutFixture>
{
    private readonly LockoutFixture _api;

    public LockoutTests(LockoutFixture api) => _api = api;

    [Fact]
    public async Task Repeated_failures_lock_the_account_out_even_for_the_right_password()
    {
        var client = _api.CreateClient();

        // The fixture configures three attempts.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var failure = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("guest", "wrong-password"), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        // The credentials are now correct and the answer is still no. Without the
        // lockout this call would succeed and the three failures would have cost
        // an attacker nothing.
        var withTheRealPassword = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("guest", ApiFactory.Password), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, withTheRealPassword.StatusCode);
    }

    [Fact]
    public async Task A_lockout_is_scoped_to_the_account_that_failed()
    {
        var client = _api.CreateClient();
        for (var attempt = 0; attempt < 4; attempt++)
            await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("supervisor", "wrong-password"), Ct);

        // Locking out a neighbour by guessing at someone else's name would be a
        // denial of service with a friendly name.
        var other = await ApiFactory.LoginAsync(client, "planner", ApiFactory.Password);

        Assert.NotEmpty(other.AccessToken);
    }
}

/// <summary>Its own database and a deliberately tiny window.</summary>
public sealed class RateLimitFixture() : ApiFactory("ratelimit", new Dictionary<string, string?>(StringComparer.Ordinal)
{
    ["Auth:AuthRequestsPerWindow"] = "3",
    ["Auth:AuthWindowSeconds"] = "60"
});

/// <summary>
/// The lockout protects one account; the rate limiter protects the rest — an
/// attacker spreading a few guesses over many user names never trips a lockout.
/// </summary>
public class RateLimitTests : IClassFixture<RateLimitFixture>
{
    private readonly RateLimitFixture _api;

    public RateLimitTests(RateLimitFixture api) => _api = api;

    [Fact]
    public async Task The_login_route_stops_answering_once_the_window_is_spent()
    {
        var client = _api.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest($"guess-{attempt}", "whatever-password"), Ct);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(3, statuses.Count(status => status == HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// Measured on the published v0.4.0: the 429 carried the status and nothing
    /// else, so a client was told "too many" and could only guess or keep
    /// hammering - the behaviour the limiter exists to prevent. RFC 6585 says a
    /// 429 SHOULD say for how long.
    /// </summary>
    [Fact]
    public async Task The_refusal_says_how_long_to_wait()
    {
        var client = _api.CreateClient();
        HttpResponseMessage? refused = null;

        for (var attempt = 0; attempt < 6 && refused is null; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest($"retry-{attempt}", "whatever-password"), Ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                refused = response;
        }

        Assert.NotNull(refused);
        var retryAfter = refused.Headers.RetryAfter;
        Assert.NotNull(retryAfter);

        // Somewhere inside the window it was told to wait for, and never zero:
        // "wait zero seconds" is the same as saying nothing.
        var seconds = retryAfter.Delta?.TotalSeconds ?? 0;
        Assert.InRange(seconds, 1, 60);
    }
}
