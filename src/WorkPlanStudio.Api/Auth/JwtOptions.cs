using System.Text;

namespace WorkPlanStudio.Api.Auth;

/// <summary>
/// How tokens are signed and how long they live.
/// <para>
/// <see cref="Validate" /> is called during start-up and throws rather than
/// warns. A signing key that is missing, too short, or still the sample value is
/// not a degraded configuration — it is an API that accepts tokens anybody can
/// mint — and the failure mode of a warning is a service that runs for months
/// looking healthy. Refusing to start is the only response that cannot be
/// ignored.
/// </para>
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// The key shipped in <c>appsettings.Development.json</c>. It is in source
    /// control, therefore public, therefore refused outside Development.
    /// </summary>
    public const string DevelopmentSigningKey = "workplan-studio-development-signing-key-change-me";

    /// <summary>Shortest key accepted: HS256 derives a 256-bit MAC key, so anything less is a false sense of security.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>The <c>iss</c> claim, and the issuer the bearer middleware requires.</summary>
    public string Issuer { get; set; } = "workplan-studio";

    /// <summary>The <c>aud</c> claim, and the audience the bearer middleware requires.</summary>
    public string Audience { get; set; } = "workplan-studio-client";

    /// <summary>The symmetric signing key. Comes from configuration — user secrets, environment, or a secret store.</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>
    /// Access-token lifetime. Short on purpose: a JWT cannot be withdrawn before
    /// it expires, so this number is how long a stolen one stays useful.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 10;

    /// <summary>Refresh-token lifetime. Long, but single-use and revocable.</summary>
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Leeway the bearer middleware allows on expiry, in seconds. Zero, unless clocks are known to drift.</summary>
    public int ClockSkewSeconds { get; set; }

    /// <summary>The signing key as bytes.</summary>
    public byte[] KeyBytes() => Encoding.UTF8.GetBytes(SigningKey);

    /// <summary>
    /// Checks the configuration and throws when the API would otherwise start in
    /// an insecure state.
    /// </summary>
    /// <param name="isDevelopment">True in the Development environment, where the sample key is allowed.</param>
    /// <exception cref="InvalidOperationException">The configuration is unusable or unsafe.</exception>
    public void Validate(bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey is not configured. Set it through user secrets, an environment variable " +
                $"({SectionName.ToUpperInvariant()}__SIGNINGKEY) or your secret store. It must be at least {MinimumKeyBytes} bytes.");

        if (Encoding.UTF8.GetByteCount(SigningKey) < MinimumKeyBytes)
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey is too short: HS256 needs at least {MinimumKeyBytes} bytes of key material.");

        if (!isDevelopment && string.Equals(SigningKey, DevelopmentSigningKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey is still the development sample key, which is published in this repository. " +
                "Configure a real key before running outside Development.");

        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException($"{SectionName}:Issuer and {SectionName}:Audience must both be set.");

        if (AccessTokenMinutes is < 1 or > 120)
            throw new InvalidOperationException($"{SectionName}:AccessTokenMinutes must be between 1 and 120.");

        if (RefreshTokenDays is < 1 or > 365)
            throw new InvalidOperationException($"{SectionName}:RefreshTokenDays must be between 1 and 365.");

        if (ClockSkewSeconds is < 0 or > 300)
            throw new InvalidOperationException($"{SectionName}:ClockSkewSeconds must be between 0 and 300.");
    }
}
