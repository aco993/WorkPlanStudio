namespace WorkPlanStudio.Api.Data;

/// <summary>
/// One issued refresh token, stored as a hash.
/// <para>
/// An access token is short-lived and self-contained, so it cannot be withdrawn
/// before it expires; the refresh token is the thing that can, which makes this
/// table the only place a session can actually be ended. Rows therefore record
/// not just validity but history — what replaced what — because a refresh token
/// that turns up after it has already been exchanged is the signature of a stolen
/// one, and the honest response to that is to end every session of the account.
/// </para>
/// <para>
/// Only the SHA-256 of the token is kept. The token itself is 256 bits of
/// cryptographic randomness with no structure to attack, so a fast hash is the
/// right one here — unlike a password, there is nothing to brute-force — and a
/// leaked copy of this database still yields no usable token.
/// </para>
/// </summary>
public sealed class RefreshToken
{
    /// <summary>Surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>The account this token belongs to.</summary>
    public string UserId { get; set; } = "";

    /// <summary>Base64 SHA-256 of the token handed to the client. Unique.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>When the token was issued.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When it stops being accepted even if never used.</summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>When it was revoked — by a rotation, a sign-out, or reuse detection.</summary>
    public DateTime? RevokedUtc { get; set; }

    /// <summary>Hash of the token issued in its place, when it was rotated.</summary>
    public string? ReplacedByHash { get; set; }

    /// <summary>Why it was revoked, for the audit trail. Free text, never a secret.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>True while the token may still be exchanged.</summary>
    public bool IsActive(DateTime utcNow) => RevokedUtc is null && ExpiresUtc > utcNow;
}
