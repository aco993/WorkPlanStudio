using System.Globalization;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services;

/// <summary>
/// Display names for the working-time model's keys. Shift keys and holiday keys
/// are stable identifiers; the words a user sees come from the resource files,
/// with the raw key as a fallback so an unknown key still renders something.
/// </summary>
public static class WorkingTimeText
{
    /// <summary>
    /// "early" → "Early shift" / "Frühschicht"; "one-shift" → "One shift".
    /// </summary>
    /// <remarks>
    /// Two rules write a <see cref="RuleApplication"/> against the <i>pattern</i>
    /// rather than a shift — § 9 (2) and the holiday half of § 11 — so this has to
    /// resolve a pattern key too. The pattern's own label carries its schedule
    /// after a middle dot ("One shift · 07:00–15:30, Mon–Fri"), which belongs in a
    /// picker and not in the middle of a sentence, so only the name is taken.
    /// </remarks>
    public static string ShiftName(this IStringLocalizer<SharedResource> localizer, string shiftKey)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        var shift = localizer[$"ShiftKey_{shiftKey}"];
        if (!shift.ResourceNotFound)
            return shift.Value;

        var pattern = localizer[$"Shift_{shiftKey}"];
        if (pattern.ResourceNotFound)
            return shiftKey;

        int dot = pattern.Value.IndexOf('·', StringComparison.Ordinal);
        return dot < 0 ? pattern.Value : pattern.Value[..dot].TrimEnd();
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

    /// <summary>The title of a rule, "§ 3 ArbZG" and all — what a legend row and a cause list show.</summary>
    public static string RuleTitle(this IStringLocalizer<SharedResource> localizer, WorkingTimeRuleId rule)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[$"Rule_{rule}_Title"];
    }

    /// <summary>The section a rule is written in, e.g. <c>§ 5 (2) ArbZG</c>.</summary>
    public static string LegalReference(WorkingTimeRuleId rule) =>
        WorkingTimeRules.Catalog.First(info => info.Id == rule).LegalReference;

    /// <summary>The § 5 (2) sector, named the way the subsection names it.</summary>
    public static string SectorName(this IStringLocalizer<SharedResource> localizer, RestExceptionSector sector)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[$"Wt_Sector_{sector}"];
    }

    /// <summary>The § 3 sentence 2 reference period, as the subsection offers it.</summary>
    public static string WindowName(this IStringLocalizer<SharedResource> localizer, AveragingWindow window)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[$"Wt_Window_{window}"];
    }

    /// <summary>
    /// A statutory deadline in the unit the statute writes it in: § 11 (3) says
    /// two weeks and § 11 (2) eight, not 336 and 1 344 hours, which is what the
    /// ordinary duration formatter would produce for the same values.
    /// </summary>
    public static string Deadline(this IStringLocalizer<SharedResource> localizer, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        int days = (int)window.TotalDays;
        if (days >= 7 && window == TimeSpan.FromDays(days) && days % 7 == 0)
            return string.Format(CultureInfo.CurrentUICulture, localizer["Wt_Weeks"], days / 7);
        if (days >= 1 && window == TimeSpan.FromDays(days))
            return string.Format(CultureInfo.CurrentUICulture, localizer["Wt_Days"], days);

        return Format.Duration(window);
    }

    /// <summary>
    /// An hours-per-Werktag figure with one decimal place: 8,33 h is the number
    /// § 3 sentence 2 is about, and rounding it to "8 h" would turn a breach into
    /// the limit itself.
    /// </summary>
    public static string WerktaeglichHours(double hours) =>
        hours.ToString("N2", CultureInfo.CurrentUICulture) + Format.NoBreak + "h";
}
