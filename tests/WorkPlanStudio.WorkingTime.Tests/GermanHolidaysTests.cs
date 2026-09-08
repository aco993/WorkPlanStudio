namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// Golden values for the holiday calendar. The 2026 tables are checked against
/// the DGB's published overview; Easter against the well-known dates. A wrong
/// holiday is the kind of bug nobody notices until a plant is closed on the
/// wrong Thursday, so these are exhaustive per state rather than sampled.
/// </summary>
public class GermanHolidaysTests
{
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2028, 4, 16)]
    [InlineData(2030, 4, 21)]
    [InlineData(2038, 4, 25)]   // the latest possible Easter this century
    public void Easter_matches_the_published_dates(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), GermanHolidays.EasterSunday(year));

    [Fact]
    public void Repentance_day_is_the_wednesday_before_23_november()
    {
        Assert.Equal(new DateOnly(2026, 11, 18), GermanHolidays.RepentanceDay(2026));
        Assert.Equal(new DateOnly(2025, 11, 19), GermanHolidays.RepentanceDay(2025));
        Assert.Equal(new DateOnly(2027, 11, 17), GermanHolidays.RepentanceDay(2027));
    }

    private static readonly DateOnly[] Nationwide2026 =
    [
        new(2026, 1, 1), new(2026, 4, 3), new(2026, 4, 6), new(2026, 5, 1), new(2026, 5, 14),
        new(2026, 5, 25), new(2026, 10, 3), new(2026, 12, 25), new(2026, 12, 26)
    ];

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Every_state_has_the_nine_nationwide_holidays(GermanState state)
    {
        var dates = GermanHolidays.ForYear(2026, state).Select(h => h.Date).ToHashSet();
        Assert.All(Nationwide2026, date => Assert.Contains(date, dates));
        Assert.Equal(9, GermanHolidays.ForYear(2026, state).Count(h => h.Scope == HolidayScope.Nationwide));
    }

    public static TheoryData<GermanState> AllStates()
    {
        var data = new TheoryData<GermanState>();
        foreach (var state in GermanStates.All)
            data.Add(state);
        return data;
    }

    [Theory]
    [InlineData(GermanState.NW, 11, "CorpusChristi,AllSaints")]
    [InlineData(GermanState.BY, 12, "Epiphany,CorpusChristi,AllSaints")]
    [InlineData(GermanState.BW, 12, "Epiphany,CorpusChristi,AllSaints")]
    [InlineData(GermanState.BE, 10, "WomensDay")]
    [InlineData(GermanState.MV, 11, "WomensDay,ReformationDay")]
    [InlineData(GermanState.SN, 11, "ReformationDay,RepentanceDay")]
    [InlineData(GermanState.ST, 11, "Epiphany,ReformationDay")]
    [InlineData(GermanState.TH, 11, "ChildrensDay,ReformationDay")]
    [InlineData(GermanState.SL, 12, "CorpusChristi,Assumption,AllSaints")]
    [InlineData(GermanState.HE, 10, "CorpusChristi")]
    [InlineData(GermanState.RP, 11, "CorpusChristi,AllSaints")]
    [InlineData(GermanState.BB, 10, "ReformationDay")]
    [InlineData(GermanState.HB, 10, "ReformationDay")]
    [InlineData(GermanState.HH, 10, "ReformationDay")]
    [InlineData(GermanState.NI, 10, "ReformationDay")]
    [InlineData(GermanState.SH, 10, "ReformationDay")]
    public void State_holidays_2026_match_the_published_tables(GermanState state, int total, string stateKeys)
    {
        var holidays = GermanHolidays.ForYear(2026, state);

        Assert.Equal(total, holidays.Count);
        Assert.Equal(
            stateKeys.Split(',').OrderBy(k => k),
            holidays.Where(h => h.Scope == HolidayScope.State).Select(h => h.Key).OrderBy(k => k));
        Assert.DoesNotContain(holidays, h => h.Scope == HolidayScope.Partial);
    }

    [Fact]
    public void Partial_holidays_are_included_only_on_request()
    {
        var bavaria = GermanHolidays.ForYear(2026, GermanState.BY, includePartial: true);
        Assert.Contains(bavaria, h => h.Key == "Assumption" && h.Date == new DateOnly(2026, 8, 15) && h.Scope == HolidayScope.Partial);
        Assert.Contains(bavaria, h => h.Key == "AugsburgPeaceFestival" && h.Date == new DateOnly(2026, 8, 8));
        Assert.Equal(14, bavaria.Count);

        var saxony = GermanHolidays.ForYear(2026, GermanState.SN, includePartial: true);
        Assert.Contains(saxony, h => h.Key == "CorpusChristi" && h.Scope == HolidayScope.Partial);
        Assert.Equal(12, saxony.Count);

        var thuringia = GermanHolidays.ForYear(2026, GermanState.TH, includePartial: true);
        Assert.Equal(12, thuringia.Count);
    }

    [Fact]
    public void Movable_feasts_follow_easter()
    {
        var nw = GermanHolidays.ForYear(2026, GermanState.NW).ToDictionary(h => h.Key);

        Assert.Equal(new DateOnly(2026, 4, 3), nw["GoodFriday"].Date);
        Assert.Equal(new DateOnly(2026, 4, 6), nw["EasterMonday"].Date);
        Assert.Equal(new DateOnly(2026, 5, 14), nw["AscensionDay"].Date);
        Assert.Equal(new DateOnly(2026, 5, 25), nw["WhitMonday"].Date);
        Assert.Equal(new DateOnly(2026, 6, 4), nw["CorpusChristi"].Date);
    }

    [Fact]
    public void Holidays_are_sorted_and_carry_both_names()
    {
        var holidays = GermanHolidays.ForYear(2026, GermanState.BY);

        Assert.Equal(holidays.OrderBy(h => h.Date), holidays);
        Assert.All(holidays, h =>
        {
            Assert.False(string.IsNullOrWhiteSpace(h.NameDe));
            Assert.False(string.IsNullOrWhiteSpace(h.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(h.Key));
        });
    }

    [Fact]
    public void Between_spans_year_boundaries()
    {
        var holidays = GermanHolidays.Between(new DateOnly(2026, 12, 20), new DateOnly(2027, 1, 6), GermanState.BY);

        Assert.Equal(["ChristmasDay", "BoxingDay", "NewYear", "Epiphany"], holidays.Select(h => h.Key));
    }

    [Fact]
    public void Between_rejects_an_inverted_range() =>
        Assert.Throws<ArgumentException>(() =>
            GermanHolidays.Between(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), GermanState.BY));
}
