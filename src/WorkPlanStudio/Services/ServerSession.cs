using System.Net;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Services;

public sealed class ServerSession(HttpClient httpClient)
{
    private string? _antiforgeryToken;
    public UserInfo? User { get; private set; }
    public bool IsAuthenticated => User is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAntiforgeryAsync(cancellationToken);
        using var response = await httpClient.GetAsync("api/auth/me", cancellationToken);
        User = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken)
            : null;
    }

    public async Task<ApiError?> LoginAsync(
        string email,
        string password,
        string? twoFactorCode = null,
        string? recoveryCode = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Post, "api/auth/login", new AuthRequest(email, password, twoFactorCode, recoveryCode), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return await ReadErrorAsync(response, cancellationToken);
        User = await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken);
        await RefreshAntiforgeryAsync(cancellationToken);
        return null;
    }

    public async Task<ApiError?> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Post, "api/auth/register", new AuthRequest(email, password), cancellationToken);
        return response.IsSuccessStatusCode ? null : await ReadErrorAsync(response, cancellationToken);
    }

    public async Task<ApiError?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post, "api/auth/password-reset/request", new PasswordResetRequest(email), cancellationToken);
        return response.IsSuccessStatusCode ? null : await ReadErrorAsync(response, cancellationToken);
    }

    public async Task<ApiError?> ConfirmPasswordResetAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest(email, token, newPassword),
            cancellationToken);
        return response.IsSuccessStatusCode ? null : await ReadErrorAsync(response, cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync<object>(HttpMethod.Post, "api/auth/logout", null, cancellationToken);
        User = null;
        await RefreshAntiforgeryAsync(cancellationToken);
    }

    public async Task<T?> GetFromJsonAsync<T>(string uri, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    public async Task<HttpResponseMessage> SendAsync<T>(
        HttpMethod method,
        string uri,
        T? body,
        CancellationToken cancellationToken = default)
    {
        if (_antiforgeryToken is null)
            await RefreshAntiforgeryAsync(cancellationToken);
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get && method != HttpMethod.Head)
            request.Headers.Add("X-CSRF-TOKEN", _antiforgeryToken);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task RefreshAntiforgeryAsync(CancellationToken cancellationToken)
    {
        var token = await httpClient.GetFromJsonAsync<AntiforgeryToken>("api/auth/antiforgery", cancellationToken);
        _antiforgeryToken = token?.Token ?? throw new InvalidOperationException("Server did not issue an antiforgery token.");
    }

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken)
        ?? new ApiError("request_failed", $"Server returned {(int)response.StatusCode}.");
}
