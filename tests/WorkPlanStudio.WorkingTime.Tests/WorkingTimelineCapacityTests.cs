using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// What the timeline is, as opposed to what it contains: the three capacity
/// states, the range it will answer for, and the kind of time it speaks.
/// </summary>
public class WorkingTimelineCapacityTests
{
    private const long Hour = 3600;

    private static readonly DateTime From = new(2026, 6, 1);
    private static readonly DateTime To = new(2026, 6, 29);

    private static WorkingTimeline Build(ShiftPattern pattern, WorkingTimeRules? rules = null, params AbsencePeriod[] absences) =>
        WorkingTimelineBuilder.Build(pattern, rules ?? WorkingTimeRules.Statutory, absences, From, To);

    // ----- a staffed pattern the rules emptied is closed, not unconstrained -----

    private static readonly ShiftPattern SundayOnly = new("sunday-only",
        [new ShiftDefinition("sun", new TimeOnly(8, 0), new TimeOnly(16, 0), WorkDays.Sunday)]);

    [Fact]
    public void A_pattern_whose_every_shift_is_clipped_away_reports_zero_capacity()
    {
        // §9 forbids this plant from running at all. Reading "no windows" as "no
        // constraint" handed the scheduler an unconstrained 24/7 machine instead.
        var timeline = Build(SundayOnly);

        var closed = Assert.IsType<WeekCapacity.Closed>(timeline.Capacity);
        Assert.Contains(WorkingTimeRuleId.SundayRest, closed.Causes);
        Assert.False(timeline.Capacity.IsUnconstrained);
        Assert.Empty(timeline.WeekWindows);
        Assert.Equal(0, timeline.WeeklyWorkingSeconds);
        Assert.Empty(timeline.CrewShifts);
    }

    [Fact]
    public void A_closed_pattern_paints_the_gantt_closed()
    {
        var timeline = Build(SundayOnly);

        var monday = timeline.Segments(From, From.AddDays(1));
        var only = Assert.Single(monday);
        Assert.Equal(SegmentKind.OffShift, only.Kind);

        var sunday = timeline.Segments(From.AddDays(6), From.AddDays(7));
        Assert.All(sunday, s => Assert.Equal(SegmentKind.Sunday, s.Kind));
        Assert.DoesNotContain(timeline.Segments(From, To), s => s.Kind == SegmentKind.Working);
    }

    [Fact]
    public void A_closed_pattern_lets_the_scheduler_place_nothing()
    {
        var calendar = Build(SundayOnly).ToMachineCalendar(From);

        Assert.True(calendar.IsClosed);
        Assert.Equal(0, calendar.LongestPlacementSeconds);

        // The engine has no way to express "never open", so it is handed a window
        // too short to hold work rather than an empty list, which it would read
        // as no constraint at all.
        var machine = calendar.ApplyTo(new MachineCapacity(1, "M"));
        Assert.NotEqual(long.MaxValue, machine.LongestPlacementSeconds);
        Assert.True(machine.LongestPlacementSeconds < 60);
        Assert.Equal(0, machine.OpenSecondsWithin(7 * 24 * Hour) / Hour);
    }

    [Fact]
    public void A_continuous_pattern_is_still_unconstrained()
    {
        var timeline = Build(ShiftPatterns.Continuous);

        Assert.IsType<WeekCapacity.Unconstrained>(timeline.Capacity);
        Assert.True(timeline.Capacity.IsUnconstrained);
        Assert.Equal(7 * 24 * Hour, timeline.WeeklyWorkingSeconds);
        Assert.Equal(long.MaxValue, timeline.ToMachineCalendar(From).LongestPlacementSeconds);
        Assert.Equal(SundayRestStatus.NotApplicable, timeline.SundayRest);
        Assert.False(timeline.ViolatesFreeSundays);
    }

    [Fact]
    public void A_staffed_week_cannot_be_built_with_no_windows() =>
        Assert.Throws<ArgumentException>(() => new WeekCapacity.Staffed([]));

    // ----- the range the timeline will answer for -----

    [Fact]
    public void Segments_outside_the_materialised_range_are_refused()
    {
        var timeline = Build(ShiftPatterns.OneShift);

        // Neujahr 2027 and the 1. Weihnachtstag 2025 used to come back as
        // ordinary working days: contiguous, sorted and wrong.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            timeline.Segments(new DateTime(2027, 1, 1), new DateTime(2027, 1, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            timeline.Segments(new DateTime(2025, 12, 25), new DateTime(2025, 12, 26)));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Segments(From.AddHours(-1), To));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Segments(From, To.AddSeconds(1)));
    }

    [Fact]
    public void The_boundaries_themselves_are_inside_the_range()
    {
        var timeline = Build(ShiftPatterns.OneShift);

        var whole = timeline.Segments(From, To);
        Assert.Equal(From, whole[0].Start);
        Assert.Equal(To, whole[^1].End);
        Assert.Empty(timeline.Segments(From, From));
        Assert.Empty(timeline.Segments(To, From));
    }

    // ----- one kind of time -----

    [Fact]
    public void Utc_bounds_are_read_as_wall_clock_and_nothing_carries_two_kinds()
    {
        var absence = new AbsencePeriod(
            new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 10, 11, 0, 0, DateTimeKind.Utc), AbsenceKind.Unplanned, "Breakdown");

        var timeline = WorkingTimelineBuilder.Build(
            ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [absence],
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 29, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateTimeKind.Unspecified, timeline.From.Kind);
        Assert.Equal(DateTimeKind.Unspecified, timeline.To.Kind);
        Assert.All(timeline.Exceptions, e =>
        {
            Assert.Equal(DateTimeKind.Unspecified, e.Start.Kind);
            Assert.Equal(DateTimeKind.Unspecified, e.End.Kind);
        });
        Assert.All(timeline.Segments(timeline.From, timeline.To), s =>
        {
            Assert.Equal(DateTimeKind.Unspecified, s.Start.Kind);
            Assert.Equal(DateTimeKind.Unspecified, s.End.Kind);
        });
    }

    [Fact]
    public void An_absence_whose_ends_belong_to_different_worlds_is_refused()
    {
        var mixed = new AbsencePeriod(
            new DateTime(2026, 6, 3, 15, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 4, 0, 0, 0), AbsenceKind.Maintenance, "Service");

        var error = Assert.Throws<ArgumentException>(mixed.Validate);
        Assert.Contains("same kind of time", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [mixed], From, To));
    }

    [Fact]
    public void Plant_time_refuses_a_zoned_reading_where_the_contract_is_strict()
    {
        Assert.Equal(DateTimeKind.Unspecified, PlantTime.Wall(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)).Kind);
        Assert.Equal(new DateTime(2026, 6, 1), PlantTime.Wall(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Throws<ArgumentException>(() =>
            PlantTime.RequireWall(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Local), "moment"));
        PlantTime.RequireWall(new DateTime(2026, 6, 1), "moment");
    }

    // ----- dates that break date libraries -----

    [Fact]
    public void A_leap_day_is_an_ordinary_working_day()
    {
        // 29 February 2028 is a Tuesday.
        var from = new DateTime(2028, 2, 21);
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [], from, from.AddDays(14));

        var leapDay = timeline.Segments(new DateTime(2028, 2, 29), new DateTime(2028, 3, 1));
        Assert.Equal(8 * Hour, leapDay.Where(s => s.Kind == SegmentKind.Working).Sum(s => (long)s.Duration.TotalSeconds));

        // The week containing it still holds five working days.
        var week = timeline.Segments(new DateTime(2028, 2, 28), new DateTime(2028, 3, 6));
        Assert.Equal(40 * Hour, week.Where(s => s.Kind == SegmentKind.Working).Sum(s => (long)s.Duration.TotalSeconds));
    }

    [Fact]
    public void An_absence_over_a_leap_day_closes_it()
    {
        var from = new DateTime(2028, 2, 21);
        var absence = new AbsencePeriod(new DateTime(2028, 2, 29), new DateTime(2028, 3, 1), AbsenceKind.Maintenance, "Leap service");
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [absence], from, from.AddDays(14));

        var leapDay = timeline.Segments(new DateTime(2028, 2, 29), new DateTime(2028, 3, 1));
        var only = Assert.Single(leapDay);
        Assert.Equal((SegmentKind.Absence, "Leap service"), (only.Kind, only.Label));
    }

    [Fact]
    public void A_range_across_the_turn_of_the_year_keeps_both_christmases_and_new_year()
    {
        var from = new DateTime(2026, 12, 21);
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [], from, new DateTime(2027, 1, 11));

        Assert.Equal(
            ["ChristmasDay", "BoxingDay", "NewYear"],
            timeline.Holidays.Select(h => h.Key));

        var newYear = timeline.Segments(new DateTime(2027, 1, 1), new DateTime(2027, 1, 2));
        Assert.Equal(SegmentKind.Holiday, Assert.Single(newYear).Kind);

        var boxingDay = timeline.Segments(new DateTime(2026, 12, 26), new DateTime(2026, 12, 27));
        Assert.Equal(SegmentKind.Holiday, Assert.Single(boxingDay).Kind);
    }

    [Fact]
    public void A_four_hundred_day_lookahead_from_december_reaches_two_christmases()
    {
        var from = new DateTime(2026, 12, 1);
        var timeline = WorkingTimelineBuilder.Build(
            ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [], from, from.AddDays(400));

        Assert.Equal(2, timeline.Holidays.Count(h => h.Key == "ChristmasDay"));
        Assert.Equal(2, timeline.Holidays.Count(h => h.Key == "NewYear"));
    }

    // ----- merged exceptions keep every cause -----

    [Fact]
    public void Overlapping_absences_keep_every_cause_even_when_one_name_contains_another()
    {
        // "Wartung Halle 2" contains both "Wartung" and "Halle", so a substring
        // test swallowed two of the three causes and reported that nothing was
        // hidden.
        var timeline = Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory with { HolidayWorkAllowed = true },
            new AbsencePeriod(new DateTime(2026, 6, 8), new DateTime(2026, 6, 10), AbsenceKind.Maintenance, "Wartung Halle 2"),
            new AbsencePeriod(new DateTime(2026, 6, 9), new DateTime(2026, 6, 11), AbsenceKind.Maintenance, "Wartung"),
            new AbsencePeriod(new DateTime(2026, 6, 10), new DateTime(2026, 6, 12), AbsenceKind.Maintenance, "Halle"));

        var exception = Assert.Single(timeline.Exceptions);
        Assert.Equal((new DateTime(2026, 6, 8), new DateTime(2026, 6, 12)), (exception.Start, exception.End));
        Assert.Equal(["Wartung Halle 2", "Wartung", "Halle"], exception.Causes);
        Assert.Equal("Wartung Halle 2; Wartung; Halle", exception.Label);
    }

    // ----- value equality -----

    [Fact]
    public void Two_patterns_spelled_alike_are_equal()
    {
        var first = new ShiftPattern("k", [new ShiftDefinition("a", new TimeOnly(6, 0), new TimeOnly(14, 0), WorkDays.Monday)]);
        var second = new ShiftPattern("k", [new ShiftDefinition("a", new TimeOnly(6, 0), new TimeOnly(14, 0), WorkDays.Monday)]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Single(new HashSet<ShiftPattern> { first, second });
        Assert.NotEqual(first, new ShiftPattern("k", [new ShiftDefinition("a", new TimeOnly(6, 0), new TimeOnly(15, 0), WorkDays.Monday)]));
    }

    [Fact]
    public void Two_calendars_built_from_the_same_inputs_are_equal()
    {
        var first = Build(ShiftPatterns.TwoShift).ToMachineCalendar(From);
        var second = Build(ShiftPatterns.TwoShift).ToMachineCalendar(From);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Building_the_same_timeline_twice_gives_the_same_answer()
    {
        var absence = new AbsencePeriod(new DateTime(2026, 6, 10, 9, 0, 0), new DateTime(2026, 6, 10, 11, 0, 0), AbsenceKind.Unplanned, "B");
        var first = Build(ShiftPatterns.ThreeShift, absences: absence);
        var second = Build(ShiftPatterns.ThreeShift, absences: absence);

        Assert.Equal(first.Capacity, second.Capacity);
        Assert.Equal(first.CrewShifts, second.CrewShifts);
        Assert.Equal(first.Exceptions, second.Exceptions);
        Assert.Equal(first.Applications, second.Applications);
        Assert.Equal(first.Segments(From, To), second.Segments(From, To));
    }

    [Fact]
    public void A_shift_that_starts_and_ends_at_the_same_time_is_not_a_full_day()
    {
        var degenerate = new ShiftDefinition("x", new TimeOnly(8, 0), new TimeOnly(8, 0), WorkDays.Monday);

        Assert.Equal(TimeSpan.Zero, degenerate.Duration);
        Assert.Throws<ArgumentException>(degenerate.Validate);
    }

    // ----- the weekly total and the materialised week agree -----

    [Fact]
    public void The_weekly_total_matches_a_week_of_segments()
    {
        // A week with no holiday: 8 to 14 June 2026.
        var timeline = Build(ShiftPatterns.ThreeShift);
        var monday = new DateTime(2026, 6, 8);

        long worked = timeline.Segments(monday, monday.AddDays(7))
            .Where(s => s.Kind == SegmentKind.Working)
            .Sum(s => (long)s.Duration.TotalSeconds);

        Assert.Equal(timeline.WeeklyWorkingSeconds, worked);
    }
}
