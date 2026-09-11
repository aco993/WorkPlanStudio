using Microsoft.AspNetCore.Identity;

namespace WorkPlanStudio.Api.Data;

/// <summary>
/// An account. Everything that makes a password safe — the hash algorithm, the
/// iteration count, the salt, the lockout counters — comes from ASP.NET Core
/// Identity rather than from this repository: password storage is exactly the
/// kind of thing that looks easy and is not, and a portfolio is a poor place to
/// demonstrate a home-made version of it.
/// </summary>
public sealed class ApiUser : IdentityUser
{
    /// <summary>The name shown in the client's account menu; falls back to the user name when empty.</summary>
    public string DisplayName { get; set; } = "";
}
