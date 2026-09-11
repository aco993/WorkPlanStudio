namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// The statutory reproductions: each of these is a case the model used to get
/// wrong, written the way the section reads rather than the way the code is
/// shaped.
/// </summary>
public class WorkingTimeSectionTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    private static readonly DateTime From = new(2026, 6, 1);
    private static readonly DateTime To = new(2026, 6, 29);

    private static WorkingTimeline Build(ShiftPattern pattern, WorkingTimeRules? rules = null) =>
        WorkingTimelineBuilder.Build(pattern, rules ?? WorkingTimeRules.Statutory, [], From, To);

    private static long At(int dayIndex, int hour, int minute = 0) => dayIndex * Day + hour * Hour + minute * 60;

    // ----- §3: the cap is per crew and calendar day -----

    [Fact]
    public void Two_shifts_of_one_crew_on_one_day_are_capped_together()
    {
        // Twelve hours on one Monday. Each block on its own is six hours and
        // passes every per-block test; the crew's day does not. The 00:00–06:00
        // block is six hours of Nachtzeit, so it is §6 (2) that sets the ceiling.
        var pattern = new ShiftPattern("p2",
        [
            new ShiftDefinition("m", new(0, 0), new(6, 0), WorkDays.Monday, "A"),
            new ShiftDefinition("n", new(17, 0), new(23, 0), WorkDays.Monday, "A")
        ]);

        var timeline = Build(pattern);

        var cut = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.NightWork);
        Assert.Equal((TimeSpan.FromHours(12), TimeSpan.FromHours(8), "n"), (cut.Before, cut.After, cut.ShiftKey));
        Assert.Equal(8 * Hour, timeline.WeeklyWorkingSeconds);
        Assert.Equal((At(0, 0), At(0, 6)), (timeline.WeekWindows[0].StartSeconds, timeline.WeekWindows[0].EndSeconds));
        Assert.Equal((At(0, 17), At(0, 19)), (timeline.WeekWindows[1].StartSeconds, timeline.WeekWindows[1].EndSeconds));
    }

    [Fact]
    public void A_crew_day_over_the_extended_ceiling_is_cut_to_ten_hours()
    {
        // The same twelve hours in a plant that also takes the §6 (2) extension,
        // so the night cap is ten as well and it is §3 that binds. Eleven hours
        // of rest between the two blocks means §5 has nothing to say, which is
        // exactly why this shape slipped through a per-block cap.
        var rules = WorkingTimeRules.Statutory with { AllowExtendedNight = true };
        var pattern = new ShiftPattern("p2",
        [
            new ShiftDefinition("m", new(0, 0), new(6, 0), WorkDays.Monday, "A"),
            new ShiftDefinition("n", new(17, 0), new(23, 0), WorkDays.Monday, "A")
        ]);

        var timeline = Build(pattern, rules);

        var cut = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.MaxDailyWorkingTime);
        Assert.Equal((TimeSpan.FromHours(12), TimeSpan.FromHours(10), "n"), (cut.Before, cut.After, cut.ShiftKey));
        Assert.Equal(10 * Hour, timeline.WeeklyWorkingSeconds);
        Assert.Equal((At(0, 17), At(0, 21)), (timeline.WeekWindows[1].StartSeconds, timeline.WeekWindows[1].EndSeconds));
    }

    [Fact]
    public void Two_crews_sharing_a_day_are_capped_separately()
    {
        // The same twelve machine-hours, split between two crews, is lawful.
        var pattern = new ShiftPattern("p2",
        [
            new ShiftDefinition("m", new(0, 0), new(6, 0), WorkDays.Monday, "A"),
            new ShiftDefinition("n", new(17, 0), new(23, 0), WorkDays.Monday, "B")
        ]);

        var timeline = Build(pattern);

        Assert.DoesNotContain(timeline.Applications, a => a.Rule == WorkingTimeRuleId.MaxDailyWorkingTime);
        Assert.Equal(12 * Hour, timeline.WeeklyWorkingSeconds);
    }

    [Fact]
    public void A_night_crew_working_twice_in_a_day_is_capped_at_the_night_value()
    {
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("early", new(0, 0), new(5, 0), WorkDays.Monday, "A"),
            new ShiftDefinition("late", new(19, 0), new(23, 30), WorkDays.Monday, "A")
        ]);

        var timeline = Build(pattern);

        // 5 h of night plus 4 h 30, of which 30 min is after 23:00: night work,
        // so §6 (2) caps the day at eight hours rather than ten.
        var cut = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.NightWork);
        Assert.Equal(TimeSpan.FromHours(8), cut.After);
        Assert.Equal(8 * Hour, timeline.WeeklyWorkingSeconds);
    }

    // ----- §4: the break is owed on working time, not on the length of the shift -----

    [Theory]
    [InlineData(6, 0, 6, 0, 0)]           // exactly six hours: nothing owed
    [InlineData(6, 1, 6, 0, 0)]           // one minute more is not one minute of working time
    [InlineData(6, 30, 6, 0, 0)]
    [InlineData(6, 31, 6, 1, 30)]         // now there is room for the break and a minute of work
    [InlineData(8, 30, 8, 0, 30)]
    [InlineData(9, 0, 8, 30, 30)]
    [InlineData(9, 30, 9, 0, 30)]
    [InlineData(9, 45, 9, 0, 30)]
    [InlineData(9, 46, 9, 1, 45)]
    [InlineData(11, 0, 10, 15, 45)]
    public void The_break_owed_is_solved_on_the_net_working_time(
        int grossHours, int grossMinutes, int netHours, int netMinutes, int breakMinutes)
    {
        var rules = WorkingTimeRules.Statutory;
        var gross = new TimeSpan(grossHours, grossMinutes, 0);

        var net = rules.NetWorkingTime(gross);

        Assert.Equal(new TimeSpan(netHours, netMinutes, 0), net);
        Assert.Equal(TimeSpan.FromMinutes(breakMinutes), rules.BreakOwed(net));
        Assert.True(net + rules.BreakOwed(net) <= gross, "the break has to fit inside the shift");
    }

    [Fact]
    public void Net_working_time_never_falls_as_the_shift_gets_longer()
    {
        // The discontinuity this pins down cost half an hour of capacity for one
        // extra minute of shift, and would mislead any search that tunes shift
        // length.
        var rules = WorkingTimeRules.Statutory;
        var previous = TimeSpan.Zero;
        for (int minutes = 0; minutes <= 24 * 60; minutes++)
        {
            var net = rules.NetWorkingTime(TimeSpan.FromMinutes(minutes));
            Assert.True(net >= previous, $"net working time fell at {minutes} min: {previous} → {net}");
            Assert.True(net <= TimeSpan.FromMinutes(minutes));
            previous = net;
        }
    }

    [Fact]
    public void One_extra_minute_of_shift_does_not_cost_half_an_hour_of_capacity()
    {
        var six = Build(new ShiftPattern("t", [new ShiftDefinition("d", new(7, 0), new(13, 0), WorkDays.Monday)]));
        var justOver = Build(new ShiftPattern("t", [new ShiftDefinition("d", new(7, 0), new(13, 1), WorkDays.Monday)]));
        var half = Build(new ShiftPattern("t", [new ShiftDefinition("d", new(7, 0), new(13, 30), WorkDays.Monday)]));

        Assert.Equal(6 * Hour, six.WeeklyWorkingSeconds);
        Assert.Equal(6 * Hour, justOver.WeeklyWorkingSeconds);
        Assert.Equal(6 * Hour, half.WeeklyWorkingSeconds);
    }

    [Fact]
    public void A_tighter_uninterrupted_stretch_does_not_invent_a_break()
    {
        // §4 sentence 3 (how long a stretch may run) and §4 sentence 1 (when a
        // break is owed) coincide at six hours in the statute but are different
        // rules. A works agreement may tighten the first alone.
        var rules = WorkingTimeRules.Statutory with { MaxWorkWithoutBreak = TimeSpan.FromHours(4) };
        var timeline = Build(new ShiftPattern("t", [new ShiftDefinition("d", new(8, 0), new(13, 0), WorkDays.Monday)]), rules);

        Assert.Equal(5 * Hour, timeline.WeeklyWorkingSeconds);
        Assert.DoesNotContain(timeline.WeekGaps, g => g.Kind == SegmentKind.Break);
        Assert.DoesNotContain(timeline.Applications, a => a.Rule == WorkingTimeRuleId.Breaks);
    }

    [Fact]
    public void A_tighter_uninterrupted_stretch_still_splits_a_long_shift()
    {
        var rules = WorkingTimeRules.Statutory with { MaxWorkWithoutBreak = TimeSpan.FromHours(3) };
        var timeline = Build(new ShiftPattern("t", [new ShiftDefinition("d", new(6, 0), new(15, 0), WorkDays.Monday)]), rules);

        Assert.All(timeline.WeekWindows, w => Assert.True(w.DurationSeconds <= 3 * Hour));
    }

    // ----- §5 (2): the shortened rest is not free -----

    [Fact]
    public void A_ten_hour_rest_outside_the_named_sectors_is_reported_as_unavailable()
    {
        var rules = WorkingTimeRules.Statutory with { MinimumRest = TimeSpan.FromHours(10) };
        var timeline = Build(ShiftPatterns.TwoShift, rules);

        var reported = timeline.Applications.Where(a => a.Rule == WorkingTimeRuleId.RestCompensation).ToList();
        Assert.NotEmpty(reported);
        Assert.All(reported, a => Assert.Equal(TimeSpan.FromHours(11), a.After));
        Assert.Equal("§ 5 (2) ArbZG", reported[0].LegalReference);
    }

    [Fact]
    public void Each_uncompensated_shortening_of_the_rest_is_reported()
    {
        // A hospital may shorten to 10 h, but every shortening has to be paid
        // back with a rest of twelve. This crew shortens twice and has only one
        // rest long enough to compensate.
        var rules = WorkingTimeRules.Statutory with
        {
            MinimumRest = TimeSpan.FromHours(10),
            RestExceptionSector = RestExceptionSector.HealthCare
        };
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("a", new(8, 0), new(12, 0), WorkDays.Monday, "A"),
            new ShiftDefinition("b", new(22, 0), new(0, 0), WorkDays.Monday, "A"),
            new ShiftDefinition("c", new(10, 0), new(14, 0), WorkDays.Tuesday, "A")
        ]);

        var timeline = Build(pattern, rules);

        var owed = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.RestCompensation);
        Assert.Equal((TimeSpan.FromHours(10), TimeSpan.FromHours(12)), (owed.Before, owed.After));
    }

    [Fact]
    public void A_rest_below_ten_hours_is_refused_outright()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (WorkingTimeRules.Statutory with { MinimumRest = TimeSpan.FromHours(9) }).Validate());
    }

    // ----- §9 (2): both directions, and only where it applies -----

    [Fact]
    public void The_sunday_rest_may_be_moved_backwards_as_well_as_forwards()
    {
        // Saturday 18:00 → Sunday 18:00, the classic Saturday-late-shift
        // arrangement §9 (2) plainly permits.
        var rules = WorkingTimeRules.Statutory with { SundayBoundaryShift = TimeSpan.FromHours(-6) };
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("day", new(6, 0), new(14, 0), WorkDays.All, "D"),
            new ShiftDefinition("night", new(22, 0), new(6, 0), WorkDays.Weekdays, "N"),
            new ShiftDefinition("sun-evening", new(19, 0), new(23, 0), WorkDays.Sunday, "S")
        ]);

        var timeline = Build(pattern, rules);

        Assert.DoesNotContain(timeline.Applications, a => a.Rule == WorkingTimeRuleId.MultiShiftRequirement);
        Assert.Contains(timeline.WeekGaps, g => g.Kind == SegmentKind.Sunday && g.StartSeconds == At(5, 18) && g.EndSeconds == At(6, 18));
        Assert.Contains(timeline.WeekWindows, w => w.StartSeconds == At(6, 19) && w.EndSeconds == At(6, 23));

        // The Saturday day shift is before the moved boundary and survives whole.
        // Read from the crew view: on the machine's side it has already merged
        // with the tail of the Friday night shift into one open stretch.
        var saturday = Assert.Single(timeline.CrewShifts, i => i.Crew == "D" && i.Day == DayOfWeek.Saturday);
        Assert.Equal((At(5, 6), At(5, 14)), (saturday.StartSeconds, saturday.EndSeconds));
        Assert.DoesNotContain(timeline.CrewShifts, i => i.Crew == "D" && i.Day == DayOfWeek.Sunday);
    }

    [Theory]
    [InlineData(-7)]
    [InlineData(7)]
    public void A_boundary_shift_beyond_six_hours_either_way_is_refused(int hours) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (WorkingTimeRules.Statutory with { SundayBoundaryShift = TimeSpan.FromHours(hours) }).Validate());

    [Fact]
    public void A_single_shift_plant_claiming_the_moved_boundary_is_warned()
    {
        var rules = WorkingTimeRules.Statutory with { SundayBoundaryShift = TimeSpan.FromHours(6) };

        var single = Build(ShiftPatterns.OneShift, rules);
        var warning = Assert.Single(single.Applications, a => a.Rule == WorkingTimeRuleId.MultiShiftRequirement);
        Assert.Equal((TimeSpan.FromHours(6), TimeSpan.Zero), (warning.Before, warning.After));
        Assert.Equal("§ 9 (2) ArbZG", warning.LegalReference);

        // A plant with a regular day and night shift is entitled to it.
        Assert.DoesNotContain(Build(ShiftPatterns.ThreeShift, rules).Applications,
            a => a.Rule == WorkingTimeRuleId.MultiShiftRequirement);

        // And without the shift there is nothing to warn about.
        Assert.DoesNotContain(Build(ShiftPatterns.OneShift).Applications,
            a => a.Rule == WorkingTimeRuleId.MultiShiftRequirement);
    }

    // ----- §11: free Sundays and the replacement rest day -----

    [Fact]
    public void Free_sundays_are_counted_from_the_calendar_not_from_a_constant()
    {
        // 2023 and 2028 have 53 Sundays; 2026 has 52. A fixed 52 is wrong for
        // about one year in seven.
        var y2026 = WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [], From, To);
        var y2023 = WorkingTimelineBuilder.Build(
            ShiftPatterns.OneShift, WorkingTimeRules.Statutory, [], new DateTime(2023, 6, 5), new DateTime(2023, 7, 3));

        Assert.Equal(52, y2026.FreeSundaysPerYear);
        Assert.Equal(53, y2023.FreeSundaysPerYear);
        Assert.Equal(53, y2026.FreeSundays(2028));
        Assert.Equal(SundayRestStatus.Intact, y2026.SundayRest);
        Assert.False(y2026.ViolatesFreeSundays);
    }

    [Fact]
    public void A_declared_rota_is_what_makes_a_section_10_plant_lawful()
    {
        var pattern = new ShiftPattern("p", [new ShiftDefinition("day", new(7, 0), new(15, 30), WorkDays.All)]);

        // Without a rota one crew works every Sunday of the year: a real breach.
        var everySunday = Build(pattern, WorkingTimeRules.Statutory with { SundayWorkAllowed = true });
        Assert.Equal(0, everySunday.FreeSundaysPerYear);
        Assert.Equal(SundayRestStatus.Breach, everySunday.SundayRest);
        Assert.True(everySunday.ViolatesFreeSundays);

        // Four crews taking one Sunday in four leave 39 free, well over the
        // fifteen §11 (1) asks for.
        var rotated = Build(pattern, WorkingTimeRules.Statutory with { SundayWorkAllowed = true, SundayRotationWeeks = 4 });
        Assert.Equal(39, rotated.FreeSundaysPerYear);
        Assert.Equal(SundayRestStatus.Rotated, rotated.SundayRest);
        Assert.False(rotated.ViolatesFreeSundays);
    }

    [Fact]
    public void Sunday_work_carries_a_replacement_rest_day_within_two_weeks()
    {
        var rules = WorkingTimeRules.Statutory with { SundayWorkAllowed = true };
        var timeline = Build(new ShiftPattern("p", [new ShiftDefinition("day", new(7, 0), new(15, 30), WorkDays.All, "A")]), rules);

        var owed = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.ReplacementRestDay);
        Assert.Equal(("A", DayOfWeek.Sunday, TimeSpan.FromDays(14)), (owed.ShiftKey, owed.Day, owed.After));
        Assert.Equal("§ 11 (2), (3) ArbZG", owed.LegalReference);
    }

    [Fact]
    public void Holiday_work_carries_a_replacement_rest_day_within_eight_weeks()
    {
        var rules = WorkingTimeRules.Statutory with { HolidayWorkAllowed = true };
        var timeline = Build(ShiftPatterns.OneShift, rules);   // Fronleichnam falls inside the range

        var owed = Assert.Single(timeline.Applications, a => a.Rule == WorkingTimeRuleId.ReplacementRestDay);
        Assert.Equal(TimeSpan.FromDays(56), owed.After);

        // Nothing is owed where the permission is not taken.
        Assert.DoesNotContain(Build(ShiftPatterns.OneShift).Applications, a => a.Rule == WorkingTimeRuleId.ReplacementRestDay);
    }

    [Fact]
    public void A_free_sunday_count_of_fifty_three_is_expressible()
    {
        (WorkingTimeRules.Statutory with { MinimumFreeSundaysPerYear = 53 }).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (WorkingTimeRules.Statutory with { MinimumFreeSundaysPerYear = 54 }).Validate());
    }

    // ----- §6: the night window has to be a window -----

    [Fact]
    public void An_empty_night_window_is_refused_instead_of_making_everything_night_work()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (WorkingTimeRules.Statutory with { NightStart = new TimeOnly(6, 0), NightEnd = new TimeOnly(6, 0) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (WorkingTimeRules.Statutory with { NightWorkThreshold = TimeSpan.FromHours(-5) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (WorkingTimeRules.Statutory with { NightWorkThreshold = TimeSpan.FromHours(8) }).Validate());   // longer than the night
    }

    [Theory]
    [InlineData(23, 0, 1, 0, false)]    // exactly 2 h of Nachtzeit: not yet night work
    [InlineData(23, 0, 1, 1, true)]     // 2 h 01 min: night work
    public void The_night_work_threshold_is_a_strict_boundary(int startHour, int startMinute, int endHour, int endMinute, bool expected)
    {
        var pattern = new ShiftPattern("p",
        [
            new ShiftDefinition("s", new(startHour, startMinute), new(endHour, endMinute), WorkDays.Monday, "A")
        ]);

        var instance = Assert.Single(Build(pattern).CrewShifts);
        Assert.Equal(expected, instance.NightWork);
    }
}
