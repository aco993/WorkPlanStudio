namespace WorkPlanStudio.Api.Auth;

/// <summary>Account-lifecycle switches that are not about tokens.</summary>
public sealed class AuthOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// Whether <c>POST /api/auth/register</c> is reachable at all.
    /// <para>
    /// Off by default, and even when on it still needs the master-data policy —
    /// this is a planning tool with a fixed set of colleagues, not a product with
    /// public sign-up. A deployed demo with open registration is an open mail
    /// relay waiting to happen.
    /// </para>
    /// </summary>
    public bool AllowRegistration { get; set; }

    /// <summary>Failed sign-ins before the account is locked out.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long a lockout lasts, in minutes.</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Requests per window allowed on the unauthenticated auth routes, per client address.</summary>
    public int AuthRequestsPerWindow { get; set; } = 10;

    /// <summary>Length of that window, in seconds.</summary>
    public int AuthWindowSeconds { get; set; } = 60;

    /// <summary>Throws when a value would produce a nonsensical or useless policy.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (MaxFailedAttempts is < 1 or > 100)
            throw new InvalidOperationException($"{SectionName}:MaxFailedAttempts must be between 1 and 100.");
        if (LockoutMinutes is < 1 or > 1440)
            throw new InvalidOperationException($"{SectionName}:LockoutMinutes must be between 1 and 1440.");
        if (AuthRequestsPerWindow is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:AuthRequestsPerWindow must be between 1 and 1000.");
        if (AuthWindowSeconds is < 1 or > 3600)
            throw new InvalidOperationException($"{SectionName}:AuthWindowSeconds must be between 1 and 3600.");
    }
}
