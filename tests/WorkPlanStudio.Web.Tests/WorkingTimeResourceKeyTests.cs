using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The working-time page builds most of its resource keys out of enum members —
/// <c>Rule_{id}_Title</c>, <c>Applied_{id}</c>, <c>Segment_{kind}</c>. A key
/// built that way and missing from the file does not fail the build and does not
/// throw: <see cref="Microsoft.Extensions.Localization.IStringLocalizer"/> falls
/// back to the key, so the page renders "Rule_ReplacementRestDay_Title" to a
/// German employer. That is how three rules shipped into the library with no
/// words at all, and it is what these tests make impossible.
/// </summary>
public sealed class WorkingTimeResourceKeyTests
{
    private static readonly ResourceFileLocalizer<object> English = new();
    private static readonly ResourceFileLocalizer<object> German = new("SharedResource.de.resx");

    /// <summary>Every key the page can derive from the model, whatever the plant does.</summary>
    private static IEnumerable<string> KeysThePageCanAskFor()
    {
        foreach (var rule in WorkingTimeRules.Catalog)
        {
            yield return $"Rule_{rule.Id}_Title";
            yield return $"Rule_{rule.Id}_Text";
            yield return $"Applied_{rule.Id}";
        }

        foreach (var kind in Enum.GetValues<SegmentKind>())
            yield return $"Segment_{kind}";
        foreach (var kind in Enum.GetValues<AbsenceKind>())
            yield return $"Absence_{kind}";
        foreach (var sector in Enum.GetValues<RestExceptionSector>())
            yield return $"Wt_Sector_{sector}";
        foreach (var window in Enum.GetValues<AveragingWindow>())
            yield return $"Wt_Window_{window}";
        foreach (var status in Enum.GetValues<SundayRestStatus>())
            yield return $"Wt_SundayStatus_{status}";
        foreach (var scope in Enum.GetValues<HolidayScope>())
            yield return $"HolidayScope_{scope}";

        foreach (var pattern in ShiftPatterns.Presets)
        {
            yield return $"Shift_{pattern.Key}";
            foreach (var shift in pattern.Shifts)
                yield return $"ShiftKey_{shift.Key}";
        }

        // The preview's operating-day choice is private to the page, so its three
        // members are named rather than enumerated. If one is renamed the page
        // renders the raw key and this list is what says so.
        yield return "Wt_Days_AsDefined";
        yield return "Wt_Days_WithSaturday";
        yield return "Wt_Days_WithSunday";
    }

    public static TheoryData<string> DerivedKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in KeysThePageCanAskFor().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(DerivedKeys))]
    public void Every_key_the_page_can_derive_resolves_in_both_languages(string key)
    {
        Assert.False(English[key].ResourceNotFound, $"English is missing {key}");
        Assert.False(German[key].ResourceNotFound, $"German is missing {key}");
    }

    /// <summary>
    /// The catalogue is what the page loops over, so a rule the library defines
    /// and forgets to catalogue would be invisible here — and would still reach
    /// the page as a <see cref="RuleApplication"/> whose <c>Applied_</c> key
    /// nobody wrote.
    /// </summary>
    [Fact]
    public void Every_rule_the_library_defines_is_in_the_catalogue()
    {
        var catalogued = WorkingTimeRules.Catalog.Select(info => info.Id).ToHashSet();
        var missing = Enum.GetValues<WorkingTimeRuleId>().Where(id => !catalogued.Contains(id)).ToArray();

        Assert.True(missing.Length == 0, $"rules with no catalogue entry: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Holiday names come from the calendar, not from an enum, and the calendar
    /// covers 1990–2200 across sixteen states. 2017 is in the sample because it
    /// is the one year the Reformationstag was nationwide.
    /// </summary>
    [Theory]
    [InlineData(2017)]
    [InlineData(2026)]
    public void Every_holiday_the_calendar_can_produce_has_a_name_in_both_languages(int year)
    {
        var missing = new List<string>();

        foreach (var state in Enum.GetValues<GermanState>())
        {
            foreach (var holiday in GermanHolidays.ForYear(year, state, includePartial: true))
            {
                var key = $"Holiday_{holiday.Key}";
                if (English[key].ResourceNotFound || German[key].ResourceNotFound)
                    missing.Add($"{key} ({state} {year})");
            }
        }

        Assert.True(missing.Count == 0, $"holidays with no name: {string.Join(", ", missing.Distinct(StringComparer.Ordinal))}");
    }

    /// <summary>
    /// The three keys IR-5 named are not <c>Wt_</c>-prefixed like the rest of
    /// this stream's additions, because the page derives them from the rule id.
    /// Renaming them would break the derivation silently, so they are pinned.
    /// </summary>
    [Theory]
    [InlineData(WorkingTimeRuleId.RestCompensation, "§ 5 (2) ArbZG")]
    [InlineData(WorkingTimeRuleId.MultiShiftRequirement, "§ 9 (2) ArbZG")]
    [InlineData(WorkingTimeRuleId.ReplacementRestDay, "§ 11 (2), (3) ArbZG")]
    public void The_three_new_rules_carry_their_section_and_their_words(WorkingTimeRuleId rule, string reference)
    {
        Assert.Equal(reference, WorkingTimeText.LegalReference(rule));
        Assert.False(German[$"Rule_{rule}_Text"].ResourceNotFound);

        // The German text is the primary one here, and a legal claim without its
        // paragraph is the thing this stream exists to remove.
        Assert.Contains("§", German[$"Rule_{rule}_Text"].Value, StringComparison.Ordinal);
    }
}
