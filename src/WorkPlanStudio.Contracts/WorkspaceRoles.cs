namespace WorkPlanStudio.Contracts;

/// <summary>
/// The three role names, as they travel on the wire and sit in a token's role
/// claim. They are the <c>ToString()</c> of the client's <c>WorkspaceRole</c>
/// enum, spelled out here so the contracts assembly stays free of any dependency
/// on the application. <c>RoleNamesMatchTheApplication</c> in the API tests fails
/// if the two ever drift apart.
/// </summary>
public static class WorkspaceRoles
{
    /// <summary>Reads everything, changes nothing. May still run a schedule.</summary>
    public const string Guest = "Guest";

    /// <summary>Runs the shop floor: releases and cancels orders, records absences.</summary>
    public const string Supervisor = "Supervisor";

    /// <summary>Owns master data and the plant rules.</summary>
    public const string Planner = "Planner";

    /// <summary>Every role, for seeding, validation and the tests.</summary>
    public static IReadOnlyList<string> All { get; } = [Guest, Supervisor, Planner];

    /// <summary>True when <paramref name="role"/> is one of the three known names (ordinal, case-sensitive).</summary>
    public static bool IsKnown(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// The authorization policy names, identical to the strings the browser client
/// already uses in <c>AuthorizeView Policy="…"</c> and at its service guard. A
/// reviewer should be able to see one authorization model rather than two, so
/// the API registers policies under exactly these names and the API tests assert
/// the list against the application's own <c>Permissions.All</c>.
/// </summary>
public static class PolicyNames
{
    /// <summary>Create, edit and delete work plans and work centers.</summary>
    public const string ManageMasterData = "ManageMasterData";

    /// <summary>Create, release, cancel and delete production orders.</summary>
    public const string ManageOrders = "ManageOrders";

    /// <summary>Change the plant's working-time rules.</summary>
    public const string ManagePlantRules = "ManagePlantRules";

    /// <summary>Record and remove work-center absences.</summary>
    public const string ManageAbsences = "ManageAbsences";

    /// <summary>Reset the stored data to the sample tenant.</summary>
    public const string ResetData = "ResetData";

    /// <summary>Every policy name.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ManageMasterData, ManageOrders, ManagePlantRules, ManageAbsences, ResetData];
}
