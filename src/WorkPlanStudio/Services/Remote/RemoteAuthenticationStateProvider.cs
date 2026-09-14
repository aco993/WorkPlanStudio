using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Services.Remote;

/// <summary>
/// The identity when a server is configured: a signed token instead of a
/// dropdown.
/// <para>
/// Everything downstream is untouched. <c>AuthorizeView</c>, the named policies
/// and <c>IPermissionGuard</c> are the same objects they were in offline mode;
/// only where the <see cref="ClaimsPrincipal"/> comes from has changed, which
/// is exactly the seam ADR 0013 said this class would occupy. The roles now
/// come from the token, so they cannot be chosen in the browser — and even if
/// someone edited them there, the server re-checks every write.
/// </para>
/// </summary>
public sealed class RemoteAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    /// <summary>The authentication type stamped on the identity, so a reader can tell it from the demo persona.</summary>
    public const string AuthenticationType = "workplan-api";

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private readonly AuthApi _auth;
    private readonly TokenStore _tokens;
    private Task<AuthenticationState>? _state;

    /// <summary>Creates the provider.</summary>
    /// <param name="auth">The unauthenticated auth endpoints.</param>
    /// <param name="tokens">The session.</param>
    public RemoteAuthenticationStateProvider(AuthApi auth, TokenStore tokens)
    {
        _auth = auth;
        _tokens = tokens;
        _tokens.Changed += OnSessionChanged;
    }

    /// <summary>The signed-in user, or <c>null</c>.</summary>
    public UserInfo? User => _tokens.User;

    /// <inheritdoc />
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // The first call is what restores a session after a page reload: the
        // store has a refresh token in sessionStorage and exchanges it.
        if (_state is null)
        {
            await _tokens.GetAccessTokenAsync();
            _state = Task.FromResult(new AuthenticationState(PrincipalFor(_tokens.User)));
        }

        return await _state;
    }

    /// <summary>Signs in with a user name and password.</summary>
    /// <param name="userName">The account name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the server accepted the credentials.</returns>
    public async Task<bool> SignInAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var tokens = await _auth.LoginAsync(userName, password, cancellationToken);
        if (tokens is null)
            return false;

        await _tokens.AcceptAsync(tokens, cancellationToken);
        return true;
    }

    /// <summary>Ends the session here and on the server.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task SignOutAsync(CancellationToken cancellationToken = default) => _tokens.SignOutAsync(cancellationToken);

    /// <summary>The principal for an identity: a name, and one role claim per role the token carries.</summary>
    /// <param name="user">The signed-in identity, or <c>null</c> for anonymous.</param>
    public static ClaimsPrincipal PrincipalFor(UserInfo? user)
    {
        if (user is null)
            return Anonymous;

        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.UserName),
                .. user.Roles.Select(role => new Claim(ClaimTypes.Role, role))
            ],
            AuthenticationType));
    }

    /// <inheritdoc />
    public void Dispose() => _tokens.Changed -= OnSessionChanged;

    private void OnSessionChanged()
    {
        _state = Task.FromResult(new AuthenticationState(PrincipalFor(_tokens.User)));
        NotifyAuthenticationStateChanged(_state);
    }
}
