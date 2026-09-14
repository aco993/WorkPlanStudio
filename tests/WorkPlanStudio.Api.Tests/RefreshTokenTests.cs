using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Its own database: reuse detection ends every session of an account.</summary>
public sealed class RefreshFixture() : ApiFactory("refresh");

/// <summary>
/// Rotation, revocation, and what happens when a refresh token turns up twice.
/// <para>
/// This is the part of a token design that is easy to get wrong quietly: a
/// refresh token that keeps working after it has been exchanged is a password
/// with no expiry, and nothing in normal use would ever reveal it.
/// </para>
/// </summary>
public class RefreshTokenTests : IClassFixture<RefreshFixture>
{
    private readonly RefreshFixture _api;

    public RefreshTokenTests(RefreshFixture api) => _api = api;

    [Fact]
    public async Task Refreshing_hands_out_a_new_pair()
    {
        var client = _api.CreateClient();
        var first = await ApiFactory.LoginAsync(client, "planner", ApiFactory.Password);

        var second = await RefreshAsync(client, first.RefreshToken);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal("planner", second.User.UserName);
        Assert.True(await CanCallMeAsync(second.AccessToken));
    }

    [Fact]
    public async Task The_rotated_token_stops_working_the_moment_it_is_exchanged()
    {
        var client = _api.CreateClient();
        var first = await ApiFactory.LoginAsync(client, "planner", ApiFactory.Password);
        await RefreshAsync(client, first.RefreshToken);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_ends_the_whole_family()
    {
        var client = _api.CreateClient();
        var first = await ApiFactory.LoginAsync(client, "supervisor", ApiFactory.Password);
        var second = await RefreshAsync(client, first.RefreshToken);

        // The old one is presented again: either the client replayed it or
        // somebody else has a copy. Both readings end the session.
        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), Ct);
        var afterwards = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(second.RefreshToken), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token()
    {
        var client = _api.CreateClient();
        var tokens = await ApiFactory.LoginAsync(client, "guest", ApiFactory.Password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken), Ct);
        client.DefaultRequestHeaders.Authorization = null;
        var afterwards = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken), Ct);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    [Fact]
    public async Task Signing_out_needs_an_access_token_of_its_own()
    {
        var client = _api.CreateClient();
        var tokens = await ApiFactory.LoginAsync(client, "guest", ApiFactory.Password);

        var response = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken), Ct);

        // Otherwise anyone holding a refresh token could end somebody's session.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_refresh_token_of_one_account_cannot_be_revoked_by_another()
    {
        var victimClient = _api.CreateClient();
        var victim = await ApiFactory.LoginAsync(victimClient, "planner", ApiFactory.Password);

        var attacker = await _api.SignedInAsync("guest");
        var logout = await attacker.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(victim.RefreshToken), Ct);

        // The call is accepted and does nothing: an error would confirm the token exists.
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var stillValid = await RefreshAsync(victimClient, victim.RefreshToken);
        Assert.NotEmpty(stillValid.AccessToken);
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_refused()
    {
        var response = await _api.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("not-a-token-anyone-ever-issued"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<AuthTokens> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken), Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthTokens>(Ct)
               ?? throw new InvalidOperationException("The refresh response was empty.");
    }

    private async Task<bool> CanCallMeAsync(string accessToken)
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await client.GetAsync("/api/auth/me", Ct)).IsSuccessStatusCode;
    }
}
