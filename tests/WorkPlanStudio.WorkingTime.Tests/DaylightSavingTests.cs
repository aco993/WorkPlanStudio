namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// The two days a year on which the plant's clock and elapsed time part company.
/// Europe/Berlin moves on 29 March 2026 (02:00 CET becomes 03:00 CEST) and back
/// on 25 October 2026 (03:00 CEST becomes 02:00 CET).
/// </summary>
public class DaylightSavingTests
{
    private static readonly TimeZoneInfo Berlin = PlantTime.Germany;

    private static readonly ShiftPattern SaturdayNight = new("p",
        [new ShiftDefinition("night", new TimeOnly(22, 0), new TimeOnly(6, 0), WorkDays.Saturday, "N")]);

    private static readonly WorkingTimeRules SundayOpen = WorkingTimeRules.Statutory with { SundayWorkAllowed = true };

    [Fact]
    public void The_zone_resolves_to_something_that_actually_changes_its_clocks()
    {
        Assert.True(Berlin.SupportsDaylightSavingTime, "Europe/Berlin was not found; the DST tests below would be vacuous");
        Assert.True(Berlin.IsInvalidTime(new DateTime(2026, 3, 29, 2, 30, 0)));
        Assert.True(Berlin.IsAmbiguousTime(new DateTime(2026, 10, 25, 2, 30, 0)));
    }

    [Fact]
    public void A_night_across_the_spring_forward_is_an_hour_shorter_than_the_clock_says()
    {
        var start = new DateTime(2026, 3, 28, 22, 0, 0);
        var end = new DateTime(2026, 3, 29, 6, 0, 0);

        Assert.Equal(TimeSpan.FromHours(8), end - start);
        Assert.Equal(TimeSpan.FromHours(7), PlantTime.RealElapsed(start, end, Berlin));
    }

    [Fact]
    public void A_night_across_the_fall_back_is_an_hour_longer_than_the_clock_says()
    {
        var start = new DateTime(2026, 10, 24, 22, 0, 0);
        var end = new DateTime(2026, 10, 25, 6, 0, 0);

        Assert.Equal(TimeSpan.FromHours(8), end - start);
        Assert.Equal(TimeSpan.FromHours(9), PlantTime.RealElapsed(start, end, Berlin));
    }

    [Fact]
    public void A_two_day_absence_across_the_spring_forward_is_forty_seven_hours()
    {
        var start = new DateTime(2026, 3, 28);
        var end = new DateTime(2026, 3, 30);

        Assert.Equal(TimeSpan.FromHours(48), end - start);
        Assert.Equal(TimeSpan.FromHours(47), PlantTime.RealElapsed(start, end, Berlin));
    }

    [Fact]
    public void A_reading_inside_the_spring_gap_maps_forward_past_it()
    {
        // 02:30 on 29 March 2026 is a time nobody's clock shows. A crew told to
        // start then starts when the clock reads 03:30.
        var instant = PlantTime.ToInstant(new DateTime(2026, 3, 29, 2, 30, 0), Berlin, preferEarlier: true);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), instant.ToUniversalTime());
    }

    [Fact]
    public void A_reading_inside_the_repeated_autumn_hour_has_two_answers()
    {
        var wall = new DateTime(2026, 10, 25, 2, 30, 0);

        var first = PlantTime.ToInstant(wall, Berlin, preferEarlier: true);
        var second = PlantTime.ToInstant(wall, Berlin, preferEarlier: false);

        Assert.Equal(TimeSpan.FromHours(1), second - first);
        Assert.Equal(TimeSpan.FromHours(2), first.Offset);   // still summer time
        Assert.Equal(TimeSpan.FromHours(1), second.Offset);
    }

    // ----- what the model says, and what the crew actually works -----

    [Fact]
    public void The_spring_night_reports_its_real_duration_not_the_clock_difference()
    {
        var timeline = WorkingTimelineBuilder.Build(
            SaturdayNight, SundayOpen, [], new DateTime(2026, 3, 23), new DateTime(2026, 3, 30));

        var onTheClock = Assert.Single(timeline.Compliance.Days);
        Assert.Equal(new DateOnly(2026, 3, 28), onTheClock.Date);
        Assert.Equal(TimeSpan.FromHours(7.5), onTheClock.ClockWorkingTime);
        Assert.Equal(TimeSpan.FromHours(7.5), onTheClock.ActualWorkingTime);

        // With the zone, the hour that does not exist stops being counted.
        var real = Assert.Single(timeline.Evaluate(Berlin).Days);
        Assert.Equal(TimeSpan.FromHours(7.5), real.ClockWorkingTime);
        Assert.Equal(TimeSpan.FromHours(6.5), real.ActualWorkingTime);
        Assert.True(real.NightWork);
        Assert.Empty(timeline.Evaluate(Berlin).Breaches);
    }

    [Fact]
    public void The_autumn_night_breaches_the_eight_hour_night_cap_it_appears_to_keep()
    {
        var timeline = WorkingTimelineBuilder.Build(
            SaturdayNight, SundayOpen, [], new DateTime(2026, 10, 19), new DateTime(2026, 10, 26));

        // On the clock the crew works 7 h 30 and the plan is compliant.
        Assert.Empty(timeline.Compliance.Breaches);
        Assert.Equal(TimeSpan.FromHours(7.5), Assert.Single(timeline.Compliance.Days).ActualWorkingTime);

        // In real hours it is 8 h 30, and §6 (2) counts real hours.
        var zoned = timeline.Evaluate(Berlin);
        Assert.Equal(TimeSpan.FromHours(8.5), Assert.Single(zoned.Days).ActualWorkingTime);

        var breach = Assert.Single(zoned.Breaches);
        Assert.Equal(WorkingTimeRuleId.NightWork, breach.Rule);
        Assert.Equal(new DateOnly(2026, 10, 24), breach.Date);
        Assert.Equal((TimeSpan.FromHours(8.5), TimeSpan.FromHours(8)), (breach.Measured, breach.Limit));
        Assert.False(zoned.IsCompliant);
    }

    [Fact]
    public void The_engine_calendar_keeps_a_week_of_wall_clock_seconds_through_a_transition()
    {
        // Pinned rather than fixed: the repeating calendar handed to the engine is
        // a week of clock positions, and a clock week is 604 800 s even when the
        // week really lasts 601 200. The engine's axis is not wall time, so the
        // shift windows still line up with the plant's clock on both sides of the
        // change; what the axis cannot express is that one of those weeks was an
        // hour shorter.
        var timeline = WorkingTimelineBuilder.Build(
            ShiftPatterns.ThreeShift, WorkingTimeRules.Statutory, [], new DateTime(2026, 3, 23), new DateTime(2026, 4, 6));

        var calendar = timeline.ToMachineCalendar(new DateTime(2026, 3, 23));

        Assert.Equal(7 * 24 * 3600, calendar.PeriodSeconds);
        Assert.Equal(TimeSpan.FromHours(167), PlantTime.RealElapsed(
            new DateTime(2026, 3, 23), new DateTime(2026, 3, 30), Berlin));
    }
}
