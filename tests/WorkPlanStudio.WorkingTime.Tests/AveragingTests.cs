namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// §3 sentence 2 lets a plant work ten hours a day only "wenn innerhalb von sechs
/// Kalendermonaten oder innerhalb von 24 Wochen im Durchschnitt acht Stunden
/// werktäglich nicht überschritten werden". That is a condition, not a checkbox,
/// and these are the cases where the condition is and is not kept.
/// </summary>
public class AveragingTests
{
    // 05:00–17:00 Monday to Friday: twelve hours of presence, cut by §3 to ten
    // hours of working time, five days a week. Fifty hours a week.
    private static readonly ShiftPattern LongDays = new("long",
        [new ShiftDefinition("day", new TimeOnly(5, 0), new TimeOnly(17, 0), WorkDays.Weekdays, "day")]);

    // Holidays off the board, so the arithmetic below is the pattern's and
    // nothing else's.
    private static readonly WorkingTimeRules NoHolidays = WorkingTimeRules.Statutory with { HolidayWorkAllowed = true };

    private static readonly DateTime Monday = new(2026, 1, 5);

    private static WorkingTimeline Build(ShiftPattern pattern, WorkingTimeRules rules, int days) =>
        WorkingTimelineBuilder.Build(pattern, rules, [], Monday, Monday.AddDays(days));

    [Fact]
    public void Fifty_hours_a_week_breaches_the_twenty_four_week_average()
    {
        var timeline = Build(LongDays, NoHolidays, 200);

        Assert.Equal(50 * 3600, timeline.WeeklyWorkingSecondsFor("day"));

        var result = Assert.Single(timeline.Compliance.Averaging);
        Assert.Equal(WorkingTimeRuleId.MaxDailyWorkingTime, result.Rule);
        Assert.Equal(AveragingBasis.MaterialisedRange, result.Basis);
        Assert.Equal(144, result.Werktage);                       // 24 weeks × 6 Werktage
        Assert.Equal(TimeSpan.FromHours(1200), result.TotalWorkingTime);
        Assert.Equal(8.333, result.AverageWerktaeglichHours, 3);
        Assert.True(result.ExceedsAverage);
        Assert.Equal(new DateOnly(2026, 1, 5), result.WindowStart);
        Assert.Equal(new DateOnly(2026, 6, 21), result.WindowEnd);
        Assert.Equal(new DateOnly(2026, 6, 21), result.FirstBreachDate);
        Assert.Equal(6, result.CompensationDaysOwed);             // 48 h over, in eight-hour days
        Assert.False(timeline.Compliance.IsCompliant);
    }

    [Fact]
    public void Closing_for_the_holidays_does_not_mend_the_average()
    {
        // Public holidays release the crew from work, so they leave the divisor
        // with the hours. Counting them as zero-hour working days would let a
        // plant average its way back into compliance by shutting for Christmas.
        var timeline = Build(LongDays, WorkingTimeRules.Statutory, 200);

        var result = Assert.Single(timeline.Compliance.Averaging);
        Assert.True(result.Werktage < 144, "holidays inside the window should leave the divisor");
        Assert.True(result.AverageWerktaeglichHours > 8);
        Assert.True(result.ExceedsAverage);
    }

    [Fact]
    public void A_forty_hour_week_keeps_the_average_with_room_to_spare()
    {
        var timeline = Build(ShiftPatterns.OneShift, NoHolidays, 200);

        var result = Assert.Single(timeline.Compliance.Averaging);
        Assert.Equal(6.667, result.AverageWerktaeglichHours, 3);
        Assert.False(result.ExceedsAverage);
        Assert.Null(result.FirstBreachDate);
        Assert.Equal(0, result.CompensationDaysOwed);
        Assert.True(timeline.Compliance.IsCompliant);
    }

    [Fact]
    public void A_range_shorter_than_the_reference_period_says_what_the_pattern_will_come_to()
    {
        // Twenty-eight days cannot measure a twenty-four week average, but the
        // week repeats, and a plant that keeps running this pattern will be
        // measured on the date the projection names.
        var timeline = Build(LongDays, NoHolidays, 28);

        var result = Assert.Single(timeline.Compliance.Averaging);
        Assert.Equal(AveragingBasis.ProjectedFromWeeklyPattern, result.Basis);
        Assert.Equal(144, result.Werktage);
        Assert.Equal(8.333, result.AverageWerktaeglichHours, 3);
        Assert.Equal(new DateOnly(2026, 6, 21), result.FirstBreachDate);
        Assert.Equal(6, result.CompensationDaysOwed);
    }

    [Fact]
    public void The_six_month_reference_period_is_the_same_promise_over_a_longer_window()
    {
        var rules = NoHolidays with { AveragingWindow = AveragingWindow.SixCalendarMonths };
        var timeline = Build(LongDays, rules, 28);

        var result = Assert.Single(timeline.Compliance.Averaging);
        Assert.Equal(156, result.Werktage);                       // 26 weeks × 6
        Assert.Equal(8.333, result.AverageWerktaeglichHours, 3);
        Assert.True(result.ExceedsAverage);
    }

    [Fact]
    public void A_three_shift_plant_is_averaged_per_crew_and_the_night_crew_twice()
    {
        var timeline = Build(ShiftPatterns.ThreeShift, NoHolidays, 200);
        var compliance = timeline.Compliance;

        Assert.Equal(["early", "late", "night"], timeline.Crews);

        // One §3 average per crew, plus the §6 (2) four-week average for the crew
        // that works nights.
        Assert.Equal(4, compliance.Averaging.Count);
        Assert.Equal(3, compliance.Averaging.Count(a => a.Rule == WorkingTimeRuleId.MaxDailyWorkingTime));
        var night = Assert.Single(compliance.Averaging, a => a.Rule == WorkingTimeRuleId.NightWork);
        Assert.Equal("night", night.Crew);
        Assert.Equal(24, night.Werktage);                         // four weeks
        Assert.Equal(6.25, night.AverageWerktaeglichHours, 3);    // 7 h 30 on five days of six

        Assert.All(compliance.Averaging, a => Assert.False(a.ExceedsAverage));
        Assert.True(compliance.IsCompliant);
    }

    [Fact]
    public void The_daily_view_is_per_crew_and_per_calendar_day()
    {
        var timeline = Build(ShiftPatterns.TwoShift, NoHolidays, 14);
        var days = timeline.Compliance.Days;

        Assert.Equal(20, days.Count);                             // two crews × ten weekdays
        Assert.All(days, d => Assert.Equal(TimeSpan.FromHours(7.5), d.ClockWorkingTime));
        Assert.All(days, d => Assert.False(d.NightWork));
        Assert.Equal(days.OrderBy(d => d.Date).ThenBy(d => d.Crew, StringComparer.Ordinal), days);
    }

    [Fact]
    public void An_absence_takes_its_hours_out_of_the_crew_day()
    {
        var absence = new AbsencePeriod(new DateTime(2026, 1, 6, 6, 0, 0), new DateTime(2026, 1, 6, 10, 0, 0), AbsenceKind.Unplanned, "Breakdown");
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, NoHolidays, [absence], Monday, Monday.AddDays(14));

        var tuesday = Assert.Single(timeline.Compliance.Days, d => d.Date == new DateOnly(2026, 1, 6));
        // The shift works 07:00–11:00 and 11:30–15:30; the breakdown takes the
        // first three hours of it.
        Assert.Equal(TimeSpan.FromHours(5), tuesday.ClockWorkingTime);
    }

    [Fact]
    public void The_compliance_view_is_computed_once()
    {
        var timeline = Build(ShiftPatterns.OneShift, NoHolidays, 200);

        Assert.Same(timeline.Compliance, timeline.Compliance);
    }

    [Fact]
    public void A_continuous_pattern_has_no_crew_and_therefore_no_average()
    {
        var timeline = Build(ShiftPatterns.Continuous, NoHolidays, 200);

        Assert.Empty(timeline.Compliance.Averaging);
        Assert.Empty(timeline.Compliance.Days);
        Assert.True(timeline.Compliance.IsCompliant);
    }
}
