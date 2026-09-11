using Microsoft.JSInterop;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Services.Remote;

/// <summary>Where the refresh token survives a page reload. Abstracted so tests need no browser.</summary>
public interface IRefreshTokenStorage
{
    /// <summary>The stored refresh token, or <c>null</c>.</summary>
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores a refresh token.</summary>
    ValueTask WriteAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored token.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The browser's <c>sessionStorage</c>.
/// <para>
/// <b>The trade-off, stated plainly:</b> anything JavaScript running on this
/// origin can read, a cross-site scripting bug can read too, and that includes
/// this token. There is no browser storage that avoids it — a token held only
/// in a JavaScript variable is just as reachable from injected script, and the
/// alternative that is not reachable, an <c>HttpOnly</c> cookie, cannot be used
/// here because the API is on another origin and the app is served from a
/// static host that can set no headers and run no same-site proxy.
/// </para>
/// <para>
/// So the exposure is accepted and bounded instead: <c>sessionStorage</c> dies
/// with the tab rather than persisting across days like <c>localStorage</c>,
/// the access token is never written here at all, and the refresh token is
/// single-use — the server retires it on first exchange and treats a second
/// use as theft. See <c>docs/adr/0020</c>.
/// </para>
/// </summary>
public sealed class SessionRefreshTokenStorage : IRefreshTokenStorage
{
    private const string Key = "workplan.refresh";
    private readonly IJSRuntime _js;

    /// <summary>Creates the storage.</summary>
    /// <param name="js">The browser's JS runtime; only the standard <c>sessionStorage</c> functions are called.</param>
    public SessionRefreshTokenStorage(IJSRuntime js) => _js = js;

    /// <inheritdoc />
    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _js.InvokeAsync<string?>("sessionStorage.getItem", cancellationToken, Key);
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(string token, CancellationToken cancellationToken = default) =>
        _js.InvokeVoidAsync("sessionStorage.setItem", cancellationToken, Key, token);

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        _js.InvokeVoidAsync("sessionStorage.removeItem", cancellationToken, Key);
}

/// <summary>
/// Holds the current session and renews it.
/// <para>
/// The access token lives in memory only and never reaches storage: it is
/// short-lived, and writing it down would double the exposure for no gain. The
/// refresh token is what is persisted, so a reload inside the same tab does not
/// throw the user back to the sign-in page.
/// </para>
/// <para>
/// Renewal is single-flight. A page that fires four requests at once will see
/// four simultaneous 401s the moment the access token expires; without the lock
/// each would start its own refresh, and because the server rotates and retires
/// refresh tokens, three of them would present a token that had just been
/// spent — which the server correctly reads as theft and answers by ending the
/// session. The lock is not an optimisation, it is what keeps rotation from
/// logging the user out.
/// </para>
/// </summary>
public sealed class TokenStore
{
    /// <summary>How long before real expiry a token is considered stale, so a request is not sent with a token about to die.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly AuthApi _auth;
    private readonly IRefreshTokenStorage _storage;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;
    private string? _refreshToken;
    private bool _restored;

    /// <summary>Creates the store.</summary>
    /// <param name="auth">The unauthenticated auth endpoints.</param>
    /// <param name="storage">Where the refresh token is kept between reloads.</param>
    /// <param name="clock">Time source; injected so expiry is testable without waiting.</param>
    public TokenStore(AuthApi auth, IRefreshTokenStorage storage, TimeProvider clock)
    {
        _auth = auth;
        _storage = storage;
        _clock = clock;
    }

    /// <summary>Who is signed in, or <c>null</c>.</summary>
    public UserInfo? User { get; private set; }

    /// <summary>Raised when the session begins, is renewed or ends.</summary>
    public event Action? Changed;

    /// <summary>Records a fresh sign-in.</summary>
    /// <param name="tokens">The pair the server issued.</param>
    /// <param name="cancellationToken">Cancels the write to storage.</param>
    public async Task AcceptAsync(AuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Store(tokens);
            await _storage.WriteAsync(tokens.RefreshToken, cancellationToken);
            _restored = true;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// A usable access token, renewing if necessary, or <c>null</c> when there
    /// is no session to renew.
    /// </summary>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_accessToken is not null && _clock.GetUtcNow() + ExpiryMargin < _accessTokenExpiresAt)
            return _accessToken;

        return await RenewAsync(staleToken: _accessToken, cancellationToken);
    }

    /// <summary>
    /// Renews the session after a request came back 401.
    /// </summary>
    /// <param name="staleToken">
    /// The access token the caller used. If it is no longer the current one,
    /// another request has already renewed and this caller simply retries with
    /// the new token instead of spending a second refresh token.
    /// </param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>The new access token, or <c>null</c> when the session is over.</returns>
    public async ValueTask<string?> RenewAsync(string? staleToken, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && !string.Equals(_accessToken, staleToken, StringComparison.Ordinal))
                return _accessToken;

            if (!_restored)
            {
                _refreshToken ??= await _storage.ReadAsync(cancellationToken);
                _restored = true;
            }

            if (_refreshToken is null)
                return null;

            var renewed = await _auth.RefreshAsync(_refreshToken, cancellationToken);
            if (renewed is null)
            {
                await ForgetAsync(cancellationToken);
                return null;
            }

            Store(renewed);
            await _storage.WriteAsync(renewed.RefreshToken, cancellationToken);
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session on the server and locally.</summary>
    /// <param name="cancellationToken">Cancels the call and the write.</param>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = _refreshToken;
        var accessToken = _accessToken;

        await ForgetAsync(cancellationToken);

        if (refreshToken is not null && accessToken is not null)
            await _auth.LogoutAsync(refreshToken, accessToken, cancellationToken);

        Changed?.Invoke();
    }

    /// <summary>Drops the session locally without calling the server. Used when a renewal was refused.</summary>
    /// <param name="cancellationToken">Cancels the write to storage.</param>
    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        _accessToken = null;
        _refreshToken = null;
        _accessTokenExpiresAt = default;
        User = null;
        _restored = true;
        await _storage.ClearAsync(cancellationToken);
    }

    private void Store(AuthTokens tokens)
    {
        _accessToken = tokens.AccessToken;
        _refreshToken = tokens.RefreshToken;
        _accessTokenExpiresAt = _clock.GetUtcNow().AddSeconds(tokens.ExpiresInSeconds);
        User = tokens.User;
    }
}
