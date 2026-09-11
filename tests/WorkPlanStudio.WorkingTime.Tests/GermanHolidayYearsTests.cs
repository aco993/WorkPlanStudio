namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// The holiday calendar is a calendar of the <i>law</i>, and the law moved several
/// times inside living memory. These are the documented changes, each checked on
/// both sides of the year it happened, plus the refusals that keep the tables
/// from answering questions they cannot know.
/// </summary>
public class GermanHolidayYearsTests
{
    [Theory]
    // Reformationstag: permanent in the five eastern states, added in the four
    // northern ones from 2018, and nationwide once for the 500th anniversary.
    [InlineData(2016, GermanState.HH, "ReformationDay", false)]
    [InlineData(2016, GermanState.NI, "ReformationDay", false)]
    [InlineData(2016, GermanState.HB, "ReformationDay", false)]
    [InlineData(2016, GermanState.SH, "ReformationDay", false)]
    [InlineData(2017, GermanState.HH, "ReformationDay", true)]   // the one-off year
    [InlineData(2018, GermanState.HH, "ReformationDay", true)]   // and permanently from here
    [InlineData(2018, GermanState.NI, "ReformationDay", true)]
    [InlineData(2016, GermanState.NW, "ReformationDay", false)]
    [InlineData(2017, GermanState.NW, "ReformationDay", true)]   // 500th anniversary, nationwide
    [InlineData(2018, GermanState.NW, "ReformationDay", false)]
    [InlineData(2017, GermanState.BY, "ReformationDay", true)]
    [InlineData(2017, GermanState.SN, "ReformationDay", true)]
    // Internationaler Frauentag: Berlin from 2019, Mecklenburg-Vorpommern from 2023.
    [InlineData(2018, GermanState.BE, "WomensDay", false)]
    [InlineData(2019, GermanState.BE, "WomensDay", true)]
    [InlineData(2022, GermanState.MV, "WomensDay", false)]
    [InlineData(2023, GermanState.MV, "WomensDay", true)]
    [InlineData(2026, GermanState.BB, "WomensDay", false)]
    // Weltkindertag: Thüringen from 2019.
    [InlineData(2018, GermanState.TH, "ChildrensDay", false)]
    [InlineData(2019, GermanState.TH, "ChildrensDay", true)]
    // Buß- und Bettag: nationwide through 1994, Sachsen only from 1995.
    [InlineData(1994, GermanState.NW, "RepentanceDay", true)]
    [InlineData(1994, GermanState.BY, "RepentanceDay", true)]
    [InlineData(1995, GermanState.NW, "RepentanceDay", false)]
    [InlineData(1995, GermanState.BY, "RepentanceDay", false)]
    [InlineData(1995, GermanState.SN, "RepentanceDay", true)]
    [InlineData(2026, GermanState.SN, "RepentanceDay", true)]
    public void A_holiday_exists_only_in_the_years_the_statute_gave_it(int year, GermanState state, string key, bool expected) =>
        Assert.Equal(expected, GermanHolidays.ForYear(year, state).Any(h => h.Key == key));

    [Fact]
    public void Nineteen_ninety_is_the_year_the_day_of_german_unity_moved()
    {
        var oldWest = GermanHolidays.ForYear(1990, GermanState.NW);
        Assert.Contains(oldWest, h => h.Key == "GermanUnityJune17" && h.Date == new DateOnly(1990, 6, 17));
        Assert.Contains(oldWest, h => h.Key == "GermanUnity" && h.Date == new DateOnly(1990, 10, 3));

        // The five new states only came into being with the accession, so the
        // June day was never theirs.
        Assert.DoesNotContain(GermanHolidays.ForYear(1990, GermanState.SN), h => h.Key == "GermanUnityJune17");
        Assert.DoesNotContain(GermanHolidays.ForYear(1991, GermanState.NW), h => h.Key == "GermanUnityJune17");
        Assert.Contains(GermanHolidays.ForYear(1991, GermanState.SN), h => h.Key == "GermanUnity");
    }

    [Theory]
    [InlineData(2019, false)]
    [InlineData(2020, true)]
    [InlineData(2021, false)]
    [InlineData(2024, false)]
    [InlineData(2025, true)]
    [InlineData(2026, false)]
    public void Berlin_had_a_one_off_eighth_of_may_in_2020_and_2025(int year, bool expected)
    {
        Assert.Equal(expected, GermanHolidays.ForYear(year, GermanState.BE).Any(h => h.Date == new DateOnly(year, 5, 8)));
        Assert.DoesNotContain(GermanHolidays.ForYear(year, GermanState.BB), h => h.Date == new DateOnly(year, 5, 8));
    }

    [Fact]
    public void Brandenburg_is_the_only_state_that_lists_easter_sunday_and_whit_sunday()
    {
        var brandenburg = GermanHolidays.ForYear(2026, GermanState.BB);
        Assert.Contains(brandenburg, h => h.Key == "EasterSunday" && h.Date == new DateOnly(2026, 4, 5));
        Assert.Contains(brandenburg, h => h.Key == "WhitSunday" && h.Date == new DateOnly(2026, 5, 24));

        foreach (var state in GermanStates.All.Where(s => s != GermanState.BB))
            Assert.DoesNotContain(GermanHolidays.ForYear(2026, state), h => h.Key is "EasterSunday" or "WhitSunday");
    }

    [Theory]
    [InlineData(1989)]
    [InlineData(1583)]
    [InlineData(2201)]
    [InlineData(9999)]
    public void A_year_the_tables_do_not_cover_is_refused(int year) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GermanHolidays.ForYear(year, GermanState.NW));

    [Fact]
    public void An_undefined_state_is_refused_rather_than_degraded_to_the_nationwide_set()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GermanHolidays.ForYear(2026, (GermanState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GermanHolidays.Between(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), (GermanState)99));
    }

    [Fact]
    public void Between_refuses_a_range_that_leaves_the_tables()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GermanHolidays.Between(new DateOnly(1583, 1, 1), new DateOnly(1999, 12, 31), GermanState.NW));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GermanHolidays.Between(new DateOnly(2199, 1, 1), new DateOnly(2201, 1, 1), GermanState.NW));

        // The first and last covered years themselves are fine.
        Assert.NotEmpty(GermanHolidays.Between(new DateOnly(1990, 1, 1), new DateOnly(1990, 12, 31), GermanState.NW));
        Assert.NotEmpty(GermanHolidays.Between(new DateOnly(2200, 1, 1), new DateOnly(2200, 12, 31), GermanState.NW));
    }

    [Theory]
    [InlineData(1583, 4, 10)]
    [InlineData(9999, 3, 28)]
    public void Easter_stays_exact_at_the_edges_of_the_gregorian_range(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), GermanHolidays.EasterSunday(year));

    [Theory]
    [InlineData(1582)]
    [InlineData(10000)]
    public void Easter_outside_the_gregorian_calendar_is_refused(int year) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GermanHolidays.EasterSunday(year));

    [Fact]
    public void Repentance_day_walks_back_from_both_sides_of_the_twenty_third()
    {
        // 23 November 2022 was itself a Wednesday, so the walk starts on the 22nd
        // and has to go a full week back.
        Assert.Equal(new DateOnly(2022, 11, 16), GermanHolidays.RepentanceDay(2022));

        // 22 November 2023 was a Wednesday, so the walk stops immediately.
        Assert.Equal(new DateOnly(2023, 11, 22), GermanHolidays.RepentanceDay(2023));

        foreach (int year in Enumerable.Range(1990, 60))
        {
            var date = GermanHolidays.RepentanceDay(year);
            Assert.Equal(DayOfWeek.Wednesday, date.DayOfWeek);
            Assert.InRange(date.Day, 16, 22);
        }
    }

    // ----- golden tables for years with a different Easter -----

    private static readonly DateOnly[] Nationwide2027 =
    [
        new(2027, 1, 1), new(2027, 3, 26), new(2027, 3, 29), new(2027, 5, 1), new(2027, 5, 6),
        new(2027, 5, 17), new(2027, 10, 3), new(2027, 12, 25), new(2027, 12, 26)
    ];

    private static readonly DateOnly[] Nationwide2038 =
    [
        new(2038, 1, 1), new(2038, 4, 23), new(2038, 4, 26), new(2038, 5, 1), new(2038, 6, 3),
        new(2038, 6, 14), new(2038, 10, 3), new(2038, 12, 25), new(2038, 12, 26)
    ];

    [Fact]
    public void The_movable_feasts_follow_easter_in_2027_and_2038()
    {
        var y2027 = GermanHolidays.ForYear(2027, GermanState.NW).Select(h => h.Date).ToHashSet();
        Assert.All(Nationwide2027, date => Assert.Contains(date, y2027));
        Assert.Contains(new DateOnly(2027, 5, 27), y2027);   // Fronleichnam

        var y2038 = GermanHolidays.ForYear(2038, GermanState.NW).Select(h => h.Date).ToHashSet();
        Assert.All(Nationwide2038, date => Assert.Contains(date, y2038));
        Assert.Contains(new DateOnly(2038, 6, 24), y2038);   // Fronleichnam after the latest possible Easter
    }

    public static TheoryData<int, GermanState, int> Totals()
    {
        // The statute is the same in 2027 and 2038 as in 2026, so the count per
        // state has to be too — only the dates move. A change to either year that
        // does not come from the law shows up here.
        (GermanState State, int Total)[] table =
        [
            (GermanState.BW, 12), (GermanState.BY, 12), (GermanState.BE, 10), (GermanState.BB, 12),
            (GermanState.HB, 10), (GermanState.HH, 10), (GermanState.HE, 10), (GermanState.MV, 11),
            (GermanState.NI, 10), (GermanState.NW, 11), (GermanState.RP, 11), (GermanState.SL, 12),
            (GermanState.SN, 11), (GermanState.ST, 11), (GermanState.SH, 10), (GermanState.TH, 11)
        ];

        var data = new TheoryData<int, GermanState, int>();
        foreach (int year in new[] { 2027, 2038 })
            foreach (var (state, total) in table)
                data.Add(year, state, total);
        return data;
    }

    [Theory]
    [MemberData(nameof(Totals))]
    public void Every_state_keeps_its_holiday_count_in_other_years(int year, GermanState state, int total)
    {
        var holidays = GermanHolidays.ForYear(year, state);

        Assert.Equal(total, holidays.Count);
        Assert.Equal(9, holidays.Count(h => h.Scope == HolidayScope.Nationwide));
        Assert.Equal(holidays.OrderBy(h => h.Date), holidays);
        Assert.Equal(holidays.Select(h => h.Date).Distinct().Count(), holidays.Count);
    }

    [Fact]
    public void The_cache_returns_the_same_list_for_the_same_question()
    {
        var first = GermanHolidays.ForYear(2031, GermanState.HE);
        var second = GermanHolidays.ForYear(2031, GermanState.HE);

        Assert.Same(first, second);
        Assert.NotSame(first, GermanHolidays.ForYear(2031, GermanState.HE, includePartial: true));
    }
}
