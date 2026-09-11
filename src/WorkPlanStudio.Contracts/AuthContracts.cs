namespace WorkPlanStudio.Contracts;

/// <summary>Credentials for <c>POST /api/auth/login</c>.</summary>
/// <param name="UserName">The account name. Case-insensitive, as ASP.NET Core Identity normalises it.</param>
/// <param name="Password">The password, in the request body and never in a URL or a log line.</param>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>
/// A new account, for <c>POST /api/auth/register</c>. The endpoint is not a public
/// sign-up: it needs the master-data policy and is off unless configuration turns
/// it on, so a deployed demo cannot be filled with accounts by strangers.
/// </summary>
/// <param name="UserName">The account name to create.</param>
/// <param name="Password">The initial password; Identity's own validator decides whether it is acceptable.</param>
/// <param name="Role">One of <see cref="WorkspaceRoles"/>.</param>
public sealed record RegisterRequest(string UserName, string Password, string Role);

/// <summary>Exchanges a refresh token for a new pair, for <c>POST /api/auth/refresh</c>.</summary>
/// <param name="RefreshToken">The opaque refresh token handed out by the previous login or refresh.</param>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Revokes one refresh token, for <c>POST /api/auth/logout</c>.</summary>
/// <param name="RefreshToken">The token to revoke. Signing out with a token already rotated away is not an error.</param>
public sealed record LogoutRequest(string RefreshToken);

/// <summary>
/// What a successful login or refresh hands back.
/// <para>
/// The access token is short-lived and meant to be held in memory; the refresh
/// token is long-lived, single-use and rotated on every exchange. The server
/// stores only a hash of the refresh token, so a copy of its database does not
/// yield usable tokens.
/// </para>
/// </summary>
/// <param name="AccessToken">A signed JWT for the <c>Authorization: Bearer</c> header.</param>
/// <param name="ExpiresInSeconds">Lifetime of <paramref name="AccessToken"/> from now, so a client can refresh ahead of a 401.</param>
/// <param name="RefreshToken">The next refresh token. The one used to obtain it is dead from this moment.</param>
/// <param name="User">Who the tokens belong to, so the client need not decode the JWT to render a name.</param>
public sealed record AuthTokens(string AccessToken, int ExpiresInSeconds, string RefreshToken, UserInfo User);

/// <summary>The signed-in identity, as returned by <c>GET /api/auth/me</c> and inside <see cref="AuthTokens"/>.</summary>
/// <param name="UserName">The account name.</param>
/// <param name="Roles">Every role the account holds; the client renders these instead of a persona.</param>
public sealed record UserInfo(string UserName, IReadOnlyList<string> Roles);
