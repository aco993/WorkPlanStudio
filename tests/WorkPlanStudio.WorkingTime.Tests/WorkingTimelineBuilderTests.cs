using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// The ArbZG cuts, one by one, on hand-built patterns where the expected clock
/// times can be checked by a human. Times are seconds from Monday 00:00 unless
/// they are wall-clock <see cref="DateTime"/>s.
/// </summary>
public class WorkingTimelineBuilderTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    // Monday 1 June 2026 .. Sunday 28 June 2026: four full weeks, contains Fronleichnam (Thu 4 June) in NW.
    private static readonly DateTime From = new(2026, 6, 1);
    private static readonly DateTime To = new(2026, 6, 29);

    private static WorkingTimeline Build(ShiftPattern pattern, WorkingTimeRules? rules = null, params AbsencePeriod[] absences) =>
        WorkingTimelineBuilder.Build(pattern, rules ?? WorkingTimeRules.Statutory, absences, From, To);

    private static ShiftPattern Single(string key, TimeOnly start, TimeOnly end, WorkDays days = WorkDays.Weekdays, string? crew = null) =>
        new("test", [new ShiftDefinition(key, start, end, days, crew)]);

    private static long At(int dayIndex, int hour, int minute = 0) => dayIndex * Day + hour * Hour + minute * 60;

    // ----- continuous -----

    [Fact]
    public void A_continuous_pattern_has_no_windows_and_one_working_segment()
    {
        var timeline = Build(ShiftPatterns.Continuous);

        Assert.Empty(timeline.WeekWindows);
        Assert.Empty(timeline.WeekGaps);
        Assert.Empty(timeline.Applications);
        Assert.Equal(7 * Day, timeline.WeeklyWorkingSeconds);
        Assert.Equal(0, timeline.FreeSundaysPerYear);

        var calendar = timeline.ToMachineCalendar(From);
        Assert.Empty(calendar.Windows);
        Assert.Equal(long.MaxValue, calendar.LongestPlacementSeconds);
    }

    // ----- §4 breaks on the one-shift preset -----

    [Fact]
    public void The_one_shift_preset_yields_eight_working_hours_with_a_half_hour_break()
    {
        var timeline = Build(ShiftPatterns.OneShift);

        // 07:00–15:30 gross 8.5 h → 30 min owed → 8 h net; the break sits after 4 h.
        Assert.Equal(10, timeline.WeekWindows.Count);
        Assert.Equal(5 * 8 * Hour, timeline.WeeklyWorkingSeconds);

        var monday = timeline.WeekWindows.Where(w => w.StartSeconds < Day).ToList();
        Assert.Equal((At(0, 7), At(0, 11)), (monday[0].StartSeconds, monday[0].EndSeconds));
        Assert.Equal((At(0, 11, 30), At(0, 15, 30)), (monday[1].StartSeconds, monday[1].EndSeconds));

        var breaks = timeline.WeekGaps.Where(g => g.Kind == SegmentKind.Break).ToList();
        Assert.Equal(5, breaks.Count);
        Assert.All(breaks, b => Assert.Equal(30 * 60, b.DurationSeconds));
        Assert.Equal(5, timeline.Applications.Count(a => a.Rule == WorkingTimeRuleId.Breaks));
        Assert.Equal(52, timeline.FreeSundaysPerYear);
        Assert.False(timeline.ViolatesFreeSundays);
    }

    [Fact]
    public void A_shift_of_six_hours_or_less_gets_no_break()
    {
        var timeline = Build(Single("short", new(8, 0), new(14, 0)));

        Assert.Equal(5, timeline.WeekWindows.Count);
        Assert.DoesNotContain(timeline.WeekGaps, g => g.Kind == SegmentKind.Break);
        Assert.Empty(timeline.Applications);
    }

    // ----- §3 daily cap -----

    [Fact]
    public void An_eleven_hour_shift_is_cut_to_ten_working_hours_with_two_breaks()
    {
        // 06:00–17:00 gross 11 h: net would be 10.25 h > 10 h cap → gross becomes
        // 10 h + 45 min = 10.75 h, ending 16:45, breaks 30 + 15 min.
        var timeline = Build(Single("long", new(6, 0), new(17, 0), WorkDays.Monday));

        var cut = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.MaxDailyWorkingTime);
        Assert.Equal(TimeSpan.FromHours(10.25), cut.Before);
        Assert.Equal(TimeSpan.FromHours(10), cut.After);
        Assert.Equal("§ 3 ArbZG", cut.LegalReference);

        Assert.Equal(At(0, 16, 45), timeline.WeekWindows.Max(w => w.EndSeconds));
        Assert.Equal(10 * Hour, timeline.WeeklyWorkingSeconds);

        var breaks = timeline.WeekGaps.Where(g => g.Kind == SegmentKind.Break).OrderBy(g => g.StartSeconds).ToList();
        Assert.Equal(2, breaks.Count);
        Assert.Equal(30 * 60, breaks[0].DurationSeconds);
        Assert.Equal(15 * 60, breaks[1].DurationSeconds);

        // No stretch of work longer than six hours.
        Assert.All(timeline.WeekWindows, w => Assert.True(w.DurationSeconds <= 6 * Hour));
    }

    [Fact]
    public void Without_the_extension_the_cap_is_eight_hours()
    {
        var rules = WorkingTimeRules.Statutory with { AllowExtendedDay = false };
        var timeline = Build(Single("long", new(6, 0), new(17, 0), WorkDays.Monday), rules);

        Assert.Equal(At(0, 14, 30), timeline.WeekWindows.Max(w => w.EndSeconds));   // 8 h + 30 min
        Assert.Equal(8 * Hour, timeline.WeeklyWorkingSeconds);
    }

    // ----- §6 night work -----

    [Fact]
    public void A_night_shift_is_capped_at_eight_hours_even_when_the_day_cap_is_ten()
    {
        // 21:00–08:00 gross 11 h, 7 h of it at night → night work → cap 8 h → ends 05:30.
        var timeline = Build(Single("night", new(21, 0), new(8, 0), WorkDays.Monday));

        var cut = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.NightWork);
        Assert.Equal(TimeSpan.FromHours(8), cut.After);
        Assert.Equal(At(1, 5, 30), timeline.WeekWindows.Max(w => w.EndSeconds));
    }

    [Fact]
    public void The_preset_night_shift_is_not_cut()
    {
        var timeline = Build(ShiftPatterns.ThreeShift);

        Assert.DoesNotContain(timeline.Applications, a => a.Rule is WorkingTimeRuleId.NightWork or WorkingTimeRuleId.MaxDailyWorkingTime);
        // Three back-to-back shifts merge into one open stretch per weekday, minus breaks.
        Assert.Equal(5 * 3 * (8 * Hour - 30 * 60), timeline.WeeklyWorkingSeconds);
    }

    // ----- §9 Sunday -----

    [Fact]
    public void A_shift_staffed_on_sunday_is_removed_when_sunday_work_is_forbidden()
    {
        var timeline = Build(Single("day", new(7, 0), new(15, 30), WorkDays.All));

        var cut = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.SundayRest);
        Assert.Equal(DayOfWeek.Sunday, cut.Day);
        Assert.Equal(TimeSpan.Zero, cut.After);
        Assert.Equal(6 * 8 * Hour, timeline.WeeklyWorkingSeconds);
        Assert.Contains(timeline.WeekGaps, g => g.Kind == SegmentKind.Sunday && g.StartSeconds == At(6, 0) && g.EndSeconds == 7 * Day);
    }

    [Fact]
    public void Sunday_work_is_kept_when_allowed_and_then_breaks_section_11()
    {
        var rules = WorkingTimeRules.Statutory with { SundayWorkAllowed = true };
        var timeline = Build(Single("day", new(7, 0), new(15, 30), WorkDays.All), rules);

        Assert.DoesNotContain(timeline.Applications, a => a.Rule == WorkingTimeRuleId.SundayRest);
        Assert.Equal(7 * 8 * Hour, timeline.WeeklyWorkingSeconds);
        Assert.Equal(0, timeline.FreeSundaysPerYear);
        Assert.True(timeline.ViolatesFreeSundays);
    }

    [Fact]
    public void A_saturday_night_shift_is_clipped_at_midnight()
    {
        var timeline = Build(Single("night", new(22, 0), new(6, 0), WorkDays.Saturday));

        var window = Assert.Single(timeline.WeekWindows);
        Assert.Equal((At(5, 22), 6 * Day), (window.StartSeconds, window.EndSeconds));
        Assert.Contains(timeline.Applications, a => a.Rule == WorkingTimeRuleId.SundayRest && a.After == TimeSpan.FromHours(2));
    }

    [Fact]
    public void The_section_9_2_boundary_shift_moves_the_closed_day()
    {
        // Sunday rest runs 06:00 Sunday → 06:00 Monday: the Saturday night shift
        // survives whole, and a Sunday-night shift into Monday is fully closed.
        var rules = WorkingTimeRules.Statutory with { SundayBoundaryShift = TimeSpan.FromHours(6) };
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("sat-night", new(22, 0), new(6, 0), WorkDays.Saturday),
            new ShiftDefinition("sun-night", new(22, 0), new(6, 0), WorkDays.Sunday)
        ]);
        var timeline = Build(pattern, rules);

        Assert.Equal(8 * Hour - 30 * 60, timeline.WeeklyWorkingSeconds);
        Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.SundayRest && a.ShiftKey == "sun-night" && a.After == TimeSpan.Zero);
        Assert.Contains(timeline.WeekGaps, g => g.Kind == SegmentKind.Sunday && g.StartSeconds == At(6, 6) && g.EndSeconds == 7 * Day);
        Assert.Contains(timeline.WeekGaps, g => g.Kind == SegmentKind.Sunday && g.StartSeconds == 0 && g.EndSeconds == At(0, 6));
    }

    // ----- §5 rest -----

    [Fact]
    public void A_crew_starting_too_soon_after_its_previous_shift_is_delayed()
    {
        // Same crew: Friday 14:00–22:00, then Saturday 06:00–14:00 — only 8 h of
        // rest. Saturday must start at 09:00.
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("fri-late", new(14, 0), new(22, 0), WorkDays.Friday, "A"),
            new ShiftDefinition("sat-early", new(6, 0), new(14, 0), WorkDays.Saturday, "A")
        ]);
        var timeline = Build(pattern);

        var rest = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.RestPeriod);
        Assert.Equal((TimeSpan.FromHours(8), TimeSpan.FromHours(11)), (rest.Before, rest.After));
        Assert.Equal(DayOfWeek.Saturday, rest.Day);

        var saturday = timeline.WeekWindows.Where(w => w.StartSeconds >= 5 * Day).ToList();
        Assert.Equal(At(5, 9), saturday.Min(w => w.StartSeconds));
        Assert.Contains(timeline.WeekGaps, g => g.Kind == SegmentKind.Rest && g.StartSeconds == At(5, 6) && g.EndSeconds == At(5, 9));
        // 5 h left → no break owed.
        Assert.DoesNotContain(timeline.WeekGaps, g => g.Kind == SegmentKind.Break && g.StartSeconds >= 5 * Day);
    }

    [Fact]
    public void The_rest_check_wraps_around_the_week()
    {
        // One crew: Sunday 20:00–04:00 (allowed) and a long Monday shift from
        // 06:00: only 2 h of rest across the week boundary, so Monday starts at
        // 15:00. (The Monday shift is capped to end 16:45 first, so 1 h 45 remain.)
        var rules = WorkingTimeRules.Statutory with { SundayWorkAllowed = true };
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("sun-night", new(20, 0), new(4, 0), WorkDays.Sunday, "A"),
            new ShiftDefinition("mon-long", new(6, 0), new(22, 0), WorkDays.Monday, "A")
        ]);
        var timeline = Build(pattern, rules);

        var rest = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.RestPeriod);
        Assert.Equal(("mon-long", TimeSpan.FromHours(2)), (rest.ShiftKey, rest.Before));
        var monday = Assert.Single(timeline.WeekWindows, w => w.Label == "mon-long");
        Assert.Equal((At(0, 15), At(0, 16, 45)), (monday.StartSeconds, monday.EndSeconds));
    }

    [Fact]
    public void A_shift_that_cannot_start_before_it_ends_is_dropped()
    {
        // Same as above but the Monday shift ends at 14:00: an 11 h rest from
        // 04:00 lands at 15:00, after the end, so the shift disappears.
        var rules = WorkingTimeRules.Statutory with { SundayWorkAllowed = true };
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("sun-night", new(20, 0), new(4, 0), WorkDays.Sunday, "A"),
            new ShiftDefinition("mon-early", new(6, 0), new(14, 0), WorkDays.Monday, "A")
        ]);
        var timeline = Build(pattern, rules);

        Assert.DoesNotContain(timeline.WeekWindows, w => w.Label == "mon-early");
        Assert.Contains(timeline.Applications, a => a.Rule == WorkingTimeRuleId.RestPeriod && a.ShiftKey == "mon-early");
    }

    [Fact]
    public void Different_crews_are_not_checked_against_each_other()
    {
        var timeline = Build(ShiftPatterns.TwoShift);

        Assert.DoesNotContain(timeline.Applications, a => a.Rule == WorkingTimeRuleId.RestPeriod);
    }

    // ----- holidays & absences -----

    [Fact]
    public void A_state_holiday_closes_the_whole_day()
    {
        var timeline = Build(ShiftPatterns.OneShift);   // NW: Fronleichnam on Thursday 4 June 2026

        var holiday = Assert.Single(timeline.Holidays);
        Assert.Equal("CorpusChristi", holiday.Key);

        var exception = Assert.Single(timeline.Exceptions);
        Assert.Equal((new DateTime(2026, 6, 4), new DateTime(2026, 6, 5), SegmentKind.Holiday), (exception.Start, exception.End, exception.Kind));

        var thursday = timeline.Segments(new DateTime(2026, 6, 4), new DateTime(2026, 6, 5));
        var only = Assert.Single(thursday);
        Assert.Equal(SegmentKind.Holiday, only.Kind);
    }

    [Fact]
    public void A_state_without_that_holiday_stays_open()
    {
        var rules = WorkingTimeRules.Statutory with { State = GermanState.BE };
        var timeline = Build(ShiftPatterns.OneShift, rules);

        Assert.Empty(timeline.Holidays);
        Assert.Contains(timeline.Segments(new DateTime(2026, 6, 4), new DateTime(2026, 6, 5)), s => s.Kind == SegmentKind.Working);
    }

    [Fact]
    public void Holiday_work_can_be_allowed()
    {
        var rules = WorkingTimeRules.Statutory with { HolidayWorkAllowed = true };

        Assert.Empty(Build(ShiftPatterns.OneShift, rules).Exceptions);
    }

    [Fact]
    public void An_absence_overlapping_a_holiday_merges_into_one_exception()
    {
        var absence = new AbsencePeriod(new DateTime(2026, 6, 3, 12, 0, 0), new DateTime(2026, 6, 5, 12, 0, 0), AbsenceKind.Maintenance, "Retooling");
        var timeline = Build(ShiftPatterns.OneShift, absences: absence);

        var exception = Assert.Single(timeline.Exceptions);
        Assert.Equal((absence.Start, absence.End), (exception.Start, exception.End));
        Assert.Equal(SegmentKind.Absence, exception.Kind);
        Assert.Contains("Retooling", exception.Label);
        Assert.Contains("CorpusChristi", exception.Label);
    }

    [Fact]
    public void Segments_are_contiguous_and_cover_the_range_exactly()
    {
        var absence = new AbsencePeriod(new DateTime(2026, 6, 10, 9, 0, 0), new DateTime(2026, 6, 10, 11, 0, 0), AbsenceKind.Unplanned, "Breakdown");
        var timeline = Build(ShiftPatterns.TwoShift, absences: absence);

        var segments = timeline.Segments(From, To);

        Assert.Equal(From, segments[0].Start);
        Assert.Equal(To, segments[^1].End);
        for (int i = 1; i < segments.Count; i++)
            Assert.Equal(segments[i - 1].End, segments[i].Start);
        Assert.All(segments, s => Assert.True(s.End > s.Start));
        Assert.Contains(segments, s => s.Kind == SegmentKind.Absence && s.Label == "Breakdown");
        Assert.Contains(segments, s => s.Kind == SegmentKind.Sunday);
        Assert.Contains(segments, s => s.Kind == SegmentKind.Holiday);
        Assert.Contains(segments, s => s.Kind == SegmentKind.Break);
        Assert.Contains(segments, s => s.Kind == SegmentKind.OffShift);
    }

    // ----- hand-over to the engine -----

    [Fact]
    public void The_machine_calendar_carries_the_phase_of_the_horizon()
    {
        var timeline = Build(ShiftPatterns.OneShift);
        var horizon = new DateTime(2026, 6, 3, 8, 0, 0);   // Wednesday 08:00

        var calendar = timeline.ToMachineCalendar(horizon);

        Assert.Equal(2 * Day + 8 * Hour, calendar.PhaseSeconds);
        Assert.Equal(7 * Day, calendar.PeriodSeconds);
        Assert.Equal(10, calendar.Windows.Count);
        Assert.Equal(45 * 60, calendar.MaxBridgeableGapSeconds);
        Assert.Equal(8 * Hour, calendar.LongestPlacementSeconds);   // 4 h + bridged break + 4 h

        var holiday = Assert.Single(calendar.Blackouts);
        Assert.Equal((16 * Hour, 40 * Hour, "CorpusChristi"), (holiday.StartSeconds, holiday.EndSeconds, holiday.Tag));
    }

    [Fact]
    public void Exceptions_before_the_horizon_are_dropped_and_straddling_ones_clipped()
    {
        var timeline = Build(ShiftPatterns.OneShift);

        Assert.Empty(timeline.ToMachineCalendar(new DateTime(2026, 6, 10)).Blackouts);

        var straddling = Assert.Single(timeline.ToMachineCalendar(new DateTime(2026, 6, 4, 12, 0, 0)).Blackouts);
        Assert.Equal((0, 12 * Hour), (straddling.StartSeconds, straddling.EndSeconds));
    }

    [Fact]
    public void The_scheduler_honours_the_calendar_end_to_end()
    {
        // Wednesday 3 June 08:00 horizon, one-shift NW machine. A 6-hour job:
        // 3 h left before the 11:00 break, bridge it, 3 h after → ends 14:30.
        // A second 6-hour job: Wednesday has 1 h left → Thursday is Fronleichnam
        // → Friday 07:00, ends 13:30.
        var horizon = new DateTime(2026, 6, 3, 8, 0, 0);
        var machine = Build(ShiftPatterns.OneShift).ToMachineCalendar(horizon).ApplyTo(new MachineCapacity(1, "Mill"));
        var jobs = new[]
        {
            new ProductionJob { Id = 1, Reference = "A", Steps = [new JobStep(10, 1, 6 * Hour)] },
            new ProductionJob { Id = 2, Reference = "B", Steps = [new JobStep(10, 1, 6 * Hour)] }
        };
        var context = new SchedulingContext(jobs, [machine], new SchedulingParameters { DispatchRule = DispatchRule.Fifo, MultiStartRuns = 1, LocalSearchMaxSteps = 0 });

        var schedule = new SchedulingEngine().Run(context).Schedule;
        var first = schedule.Operations.Single(o => o.JobId == 1);
        var second = schedule.Operations.Single(o => o.JobId == 2);

        Assert.Equal((0L, 6 * Hour + 30 * 60, 30 * 60), (first.StartSeconds, first.EndSeconds, first.PausedSeconds));
        Assert.Equal(horizon.AddDays(2).AddHours(-1), horizon.AddSeconds(second.StartSeconds));   // Friday 07:00
        Assert.Equal(6 * Hour + 30 * 60, second.DurationSeconds);
    }

    [Fact]
    public void Ranges_are_validated()
    {
        Assert.Throws<ArgumentException>(() => WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [], To, From));
        Assert.Throws<ArgumentException>(() => WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [], From, From.AddDays(4000)));
    }
}
