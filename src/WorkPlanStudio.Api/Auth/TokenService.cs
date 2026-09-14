using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Auth;

/// <summary>What a refresh attempt produced.</summary>
public enum RefreshOutcome
{
    /// <summary>The token was valid and has been exchanged for a new pair.</summary>
    Rotated,

    /// <summary>No such token, or it has expired.</summary>
    Unknown,

    /// <summary>
    /// The token was real but already spent. Treated as theft: every live session
    /// of that account is ended, because the legitimate holder and whoever else
    /// has a copy cannot be told apart.
    /// </summary>
    Reused
}

/// <summary>The result of a refresh attempt.</summary>
/// <param name="Outcome">Whether the exchange happened, and why not if it did not.</param>
/// <param name="Tokens">The new pair, when <paramref name="Outcome"/> is <see cref="RefreshOutcome.Rotated"/>.</param>
public sealed record RefreshResult(RefreshOutcome Outcome, AuthTokens? Tokens);

/// <summary>
/// Mints access tokens and manages the refresh-token ledger.
/// <para>
/// No cryptography is invented here: the signature comes from the IdentityModel
/// JWT handler and the token material from <see cref="RandomNumberGenerator"/>.
/// </para>
/// </summary>
public sealed class TokenService
{
    /// <summary>Bytes of randomness in a refresh token. 256 bits, from the OS CSPRNG.</summary>
    public const int RefreshTokenBytes = 32;

    private readonly ApiDbContext _db;
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TokenService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="db">The store holding the refresh-token ledger.</param>
    /// <param name="options">Signing key and lifetimes.</param>
    /// <param name="clock">Time source; injected so token expiry is testable without sleeping.</param>
    /// <param name="logger">Structured log sink. Tokens and hashes are never written to it.</param>
    public TokenService(ApiDbContext db, IOptions<JwtOptions> options, TimeProvider clock, ILogger<TokenService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Issues a fresh access/refresh pair and records the refresh token.
    /// </summary>
    /// <param name="user">The account signing in.</param>
    /// <param name="roles">Its roles; they become role claims and drive every policy.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The pair to hand to the client. The refresh token is returned once and never stored in clear.</returns>
    public async Task<AuthTokens> IssueAsync(ApiUser user, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);

        var now = _clock.GetUtcNow().UtcDateTime;
        var access = CreateAccessToken(user, roles, now);
        var refresh = CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(refresh),
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(_options.RefreshTokenDays)
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new AuthTokens(
            access,
            _options.AccessTokenMinutes * 60,
            refresh,
            new UserInfo(user.UserName ?? "", roles));
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, retiring the one presented.
    /// </summary>
    /// <param name="refreshToken">The token the client holds.</param>
    /// <param name="rolesOf">How to read an account's roles; supplied by the caller so this type need not know about Identity's stores.</param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    /// <returns>The outcome, and the new pair when the exchange succeeded.</returns>
    public async Task<RefreshResult> RefreshAsync(
        string refreshToken,
        Func<ApiUser, CancellationToken, Task<IReadOnlyList<string>>> rolesOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rolesOf);

        if (string.IsNullOrWhiteSpace(refreshToken))
            return new RefreshResult(RefreshOutcome.Unknown, null);

        // Looked up by hash through a unique index. No constant-time comparison
        // is added on top: the secret is 256 bits of CSPRNG output with no
        // structure, so there is no prefix to walk and nothing a timing signal on
        // an index probe could be used to recover. Constant-time matters where
        // the secret is guessable; here it would be decoration.
        var hash = Hash(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null)
            return new RefreshResult(RefreshOutcome.Unknown, null);

        var now = _clock.GetUtcNow().UtcDateTime;

        if (stored.RevokedUtc is not null)
        {
            // Presented after it was already spent. Either the client replayed an
            // old token or somebody else has a copy; there is no way to tell, and
            // the safe reading of an ambiguous signal is the hostile one.
            var killed = await RevokeAllAsync(stored.UserId, "reuse-detected", cancellationToken);
            _logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; {RevokedCount} active sessions revoked", stored.UserId, killed);
            return new RefreshResult(RefreshOutcome.Reused, null);
        }

        if (stored.ExpiresUtc <= now)
            return new RefreshResult(RefreshOutcome.Unknown, null);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken);
        if (user is null)
            return new RefreshResult(RefreshOutcome.Unknown, null);

        var roles = await rolesOf(user, cancellationToken);
        var next = CreateRefreshToken();
        var nextHash = Hash(next);

        stored.RevokedUtc = now;
        stored.RevokedReason = "rotated";
        stored.ReplacedByHash = nextHash;

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = nextHash,
            CreatedUtc = now,
            // Rotation does not extend the family: a session still ends when the
            // original refresh token would have expired.
            ExpiresUtc = stored.ExpiresUtc
        });
        await _db.SaveChangesAsync(cancellationToken);

        var tokens = new AuthTokens(
            CreateAccessToken(user, roles, now),
            _options.AccessTokenMinutes * 60,
            next,
            new UserInfo(user.UserName ?? "", roles));

        return new RefreshResult(RefreshOutcome.Rotated, tokens);
    }

    /// <summary>
    /// Revokes one refresh token. Signing out with a token that was already
    /// rotated away is not an error — the session is over either way, and a
    /// distinguishable failure would only tell a caller which tokens exist.
    /// </summary>
    /// <param name="refreshToken">The token to revoke.</param>
    /// <param name="userId">The account the caller is authenticated as; a token belonging to anyone else is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task RevokeAsync(string refreshToken, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var hash = Hash(refreshToken);
        var stored = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, cancellationToken);

        if (stored is null || stored.RevokedUtc is not null)
            return;

        stored.RevokedUtc = _clock.GetUtcNow().UtcDateTime;
        stored.RevokedReason = "signed-out";
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Revokes every live refresh token of an account.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="reason">Short audit note, e.g. "reuse-detected".</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many tokens were revoked.</returns>
    public async Task<int> RevokeAllAsync(string userId, string reason, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var live = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedUtc == null && t.ExpiresUtc > now)
            .ToListAsync(cancellationToken);

        foreach (var token in live)
        {
            token.RevokedUtc = now;
            token.RevokedReason = reason;
        }

        if (live.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return live.Count;
    }

    /// <summary>The Base64 SHA-256 of a token, which is all the server keeps.</summary>
    /// <param name="token">The token as handed to the client.</param>
    public static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A new refresh token: <see cref="RefreshTokenBytes"/> bytes of CSPRNG output, URL-safe.</summary>
    public static string CreateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

    private string CreateAccessToken(ApiUser user, IReadOnlyList<string> roles, DateTime now)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(ClaimTypes.Name, user.UserName ?? ""),
            .. roles.Select(role => new Claim(ClaimTypes.Role, role))
        ]);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = identity,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(_options.AccessTokenMinutes),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_options.KeyBytes()), SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
