using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Api.Validation;

/// <summary>
/// The plant-settings rules, duplicated.
/// <para>
/// Every other validator in this API is the application's own file, compiled in
/// (see the project file) so that a rule cannot mean two different things on two
/// hosts. This one could not be: the browser app keeps <c>PlantSettingsValidator</c>
/// in the same file as the service that writes to browser storage, and that
/// service cannot be compiled on a server. The rules below are therefore a copy,
/// kept honest by <c>PlantSettingsRulesMatchTheApplication</c> in the API tests,
/// which reads the application's source and fails if the bounds drift.
/// </para>
/// <para>
/// The fix is a one-line move in the browser app — lift the validator into
/// <c>Validation/</c> beside the other three — which is somebody else's file
/// while these streams run in parallel.
/// </para>
/// </summary>
public static class PlantSettingsRules
{
    /// <summary>Lowest legal Sunday-boundary shift, ArbZG §9 (2).</summary>
    public const int MinSundayBoundaryShiftHours = 0;

    /// <summary>Highest legal Sunday-boundary shift, ArbZG §9 (2).</summary>
    public const int MaxSundayBoundaryShiftHours = 6;

    /// <summary>Shortest rest between working days, ArbZG §5 with the reduction.</summary>
    public const int MinRestHours = 10;

    /// <summary>Standard rest between working days, ArbZG §5.</summary>
    public const int MaxRestHours = 11;

    /// <summary>Checks a settings row against the statutory bounds.</summary>
    /// <param name="settings">The candidate row.</param>
    /// <returns>The failures, empty when the row is acceptable.</returns>
    public static IReadOnlyList<ValidationIssue> Validate(PlantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<ValidationIssue>();

        if (!Enum.TryParse<GermanState>(settings.State?.Trim(), ignoreCase: true, out _))
            issues.Add(new(nameof(settings.State), "Val_StateInvalid"));
        if (settings.SundayBoundaryShiftHours < MinSundayBoundaryShiftHours ||
            settings.SundayBoundaryShiftHours > MaxSundayBoundaryShiftHours)
            issues.Add(new(nameof(settings.SundayBoundaryShiftHours), "Val_Range",
                MinSundayBoundaryShiftHours, MaxSundayBoundaryShiftHours));
        if (settings.MinimumRestHours < MinRestHours || settings.MinimumRestHours > MaxRestHours)
            issues.Add(new(nameof(settings.MinimumRestHours), "Val_Range", MinRestHours, MaxRestHours));

        return issues;
    }
}
