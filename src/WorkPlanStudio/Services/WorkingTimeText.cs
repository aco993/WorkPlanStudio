using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;

namespace WorkPlanStudio.Services;

/// <summary>
/// Display names for the working-time model's keys. Shift keys and holiday keys
/// are stable identifiers; the words a user sees come from the resource files,
/// with the raw key as a fallback so an unknown key still renders something.
/// </summary>
public static class WorkingTimeText
{
    /// <summary>"early" → "Early shift" / "Frühschicht".</summary>
    public static string ShiftName(this IStringLocalizer<SharedResource> localizer, string shiftKey)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var localized = localizer[$"ShiftKey_{shiftKey}"];
        return localized.ResourceNotFound ? shiftKey : localized.Value;
    }

    /// <summary>"CorpusChristi; Spindle service" → "Corpus Christi; Spindle service" — holiday keys localised, other labels kept.</summary>
    public static string HolidayLabel(this IStringLocalizer<SharedResource> localizer, string label)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return string.Join("; ", label.Split("; ").Select(part =>
        {
            var localized = localizer[$"Holiday_{part}"];
            return localized.ResourceNotFound ? part : localized.Value;
        }));
    }
}
