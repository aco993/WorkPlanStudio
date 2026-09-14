using WorkPlanStudio.Models;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Validation;

/// <summary>
/// Business rules for the plant settings.
/// <para>
/// It lives beside the other validators rather than beside the service that
/// writes it because the same rules have to hold on a server that has no browser
/// storage: the API's <c>PlantSettingsRules</c> was a hand-kept copy for exactly
/// as long as this file did not exist.
/// </para>
/// </summary>
public static class PlantSettingsValidator
{
    /// <summary>Checks a settings row against the statutory bounds.</summary>
    /// <param name="settings">The candidate row.</param>
    /// <returns>The failures, empty when the row is acceptable.</returns>
    public static IReadOnlyList<ValidationIssue> Validate(PlantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<ValidationIssue>();

        if (!Enum.TryParse<GermanState>(settings.State?.Trim(), ignoreCase: true, out _))
            issues.Add(new(nameof(settings.State), "Val_StateInvalid"));
        if (settings.SundayBoundaryShiftHours is < 0 or > 6)
            issues.Add(new(nameof(settings.SundayBoundaryShiftHours), "Val_Range", 0, 6));
        if (settings.MinimumRestHours is < 10 or > 11)
            issues.Add(new(nameof(settings.MinimumRestHours), "Val_Range", 10, 11));
        if (settings.SundayRotationWeeks is < 1 or > 52)
            issues.Add(new(nameof(settings.SundayRotationWeeks), "Val_Range", 1, 52));

        if (!Enum.IsDefined(settings.AveragingWindow))
            issues.Add(new(nameof(settings.AveragingWindow), "Wt_Val_AveragingWindowInvalid"));
        if (!Enum.IsDefined(settings.RestExceptionSector))
            issues.Add(new(nameof(settings.RestExceptionSector), "Wt_Val_SectorInvalid"));

        // § 5 (2) is an entitlement of the sectors it names, not a dial. Hiding
        // the control would leave the permission granted to anything else that
        // can write the row — an import, the API, a hand-edited payload — so the
        // refusal is stated here as well as in the CHECK constraint on the table.
        if (settings.MinimumRestHours < 11 && settings.RestExceptionSector == RestExceptionSector.None)
            issues.Add(new(nameof(settings.MinimumRestHours), "Wt_Val_RestNeedsSector"));

        return issues;
    }
}
