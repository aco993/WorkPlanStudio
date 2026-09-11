using System.Net.Http.Headers;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Services.Remote;

/// <summary>
/// The three auth calls, over an <see cref="HttpClient"/> that deliberately has
/// no bearer handler attached.
/// <para>
/// Keeping them off the authenticated client is what breaks the circle: the
/// handler that renews a token cannot itself depend on a client whose every
/// request goes through that handler. Sign-out is the exception that proves it
/// — it needs an access token, so it is passed one explicitly rather than
/// fetched from the store the handler owns.
/// </para>
/// </summary>
public sealed class AuthApi
{
    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="http">A plain client whose base address is the API root.</param>
    public AuthApi(HttpClient http) => _http = http;

    /// <summary>Exchanges credentials for a token pair.</summary>
    /// <param name="userName">The account name.</param>
    /// <param name="password">The password. Sent in the body, never in a URL, and never logged.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pair, or <c>null</c> when the credentials were refused.</returns>
    public async Task<AuthTokens?> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/auth/login", new LoginRequest(userName, password), cancellationToken);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AuthTokens>(cancellationToken)
            : null;
    }

    /// <summary>Exchanges a refresh token for a new pair.</summary>
    /// <param name="refreshToken">The token held by the client.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new pair, or <c>null</c> when the session is over.</returns>
    public async Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/auth/refresh", new RefreshRequest(refreshToken), cancellationToken);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AuthTokens>(cancellationToken)
            : null;
    }

    /// <summary>
    /// Revokes a refresh token. Failures are swallowed: the client has already
    /// forgotten the session, and a user who pressed sign-out must not be shown
    /// an error because the network was down.
    /// </summary>
    /// <param name="refreshToken">The token to revoke.</param>
    /// <param name="accessToken">The access token that authenticates the call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task LogoutAsync(string refreshToken, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout")
        {
            Content = JsonContent.Create(new LogoutRequest(refreshToken))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Nothing to recover: the tokens are gone locally either way, and the
            // refresh token expires on its own.
        }
    }
}
