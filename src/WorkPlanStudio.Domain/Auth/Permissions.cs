using Microsoft.AspNetCore.Authorization;

namespace WorkPlanStudio.Services.Auth;

/// <summary>
/// The personas a user can work as. The app has no accounts — it is a static
/// site — so the persona is chosen in the UI and remembered per browser. What
/// each may do is decided by the policies in <see cref="Permissions"/>, not
/// by the enum: pages and services ask "may I", never "is this a planner".
/// </summary>
public enum WorkspaceRole
{
    /// <summary>Read everything, change nothing. Can still run a schedule and use the assistant.</summary>
    Guest,

    /// <summary>Runs the shop floor: releases and cancels orders, records absences. No master data.</summary>
    Supervisor,

    /// <summary>Owns master data and the plant rules. Everything.</summary>
    Planner
}

/// <summary>
/// Named authorization policies and which roles satisfy them. Registered with
/// the standard ASP.NET Core authorization pipeline, so <c>AuthorizeView</c>,
/// <c>[Authorize]</c> and <c>IAuthorizationService</c> all work unchanged — the
/// only thing that is demo-specific is where the identity comes from.
/// </summary>
public static class Permissions
{
    /// <summary>Create, edit and delete work plans and work centers.</summary>
    public const string ManageMasterData = "ManageMasterData";

    /// <summary>Create, release, cancel and delete production orders.</summary>
    public const string ManageOrders = "ManageOrders";

    /// <summary>Change the plant's working-time rules.</summary>
    public const string ManagePlantRules = "ManagePlantRules";

    /// <summary>Record and remove work-center absences.</summary>
    public const string ManageAbsences = "ManageAbsences";

    /// <summary>Reset the browser database to the sample data.</summary>
    public const string ResetData = "ResetData";

    /// <summary>Every policy, for tests and the persona menu.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ManageMasterData, ManageOrders, ManagePlantRules, ManageAbsences, ResetData];

    /// <summary>The roles each policy accepts. One table, read by the registration and by the UI.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<WorkspaceRole>> RolesFor { get; } =
        new Dictionary<string, IReadOnlyList<WorkspaceRole>>
        {
            [ManageMasterData] = [WorkspaceRole.Planner],
            [ManageOrders] = [WorkspaceRole.Planner, WorkspaceRole.Supervisor],
            [ManagePlantRules] = [WorkspaceRole.Planner],
            [ManageAbsences] = [WorkspaceRole.Planner, WorkspaceRole.Supervisor],
            [ResetData] = [WorkspaceRole.Planner]
        };

    /// <summary>True when <paramref name="role"/> satisfies <paramref name="policy"/> — the same answer the pipeline gives.</summary>
    public static bool Grants(WorkspaceRole role, string policy) =>
        RolesFor.TryGetValue(policy, out var roles) && roles.Contains(role);

    /// <summary>Registers every policy as a role requirement.</summary>
    public static void AddWorkspacePolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (policy, roles) in RolesFor)
            options.AddPolicy(policy, builder => builder.RequireRole(roles.Select(r => r.ToString()).ToArray()));
    }
}
