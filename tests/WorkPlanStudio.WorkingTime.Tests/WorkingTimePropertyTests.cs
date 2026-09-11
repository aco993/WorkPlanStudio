using CsCheck;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// Invariants of the working-time model over random shift patterns, rules and
/// absences: the statutory limits hold in every week the builder produces, and
/// what it hands to the engine is always a valid, schedulable calendar.
/// <para>
/// The ranges the generator draws from deliberately include a daylight-saving
/// week and the turn of the year, and the patterns deliberately include ones
/// every rule can clip away — the properties have to hold there too, and two of
/// them used to return early on exactly that case.
/// </para>
/// </summary>
public class WorkingTimePropertyTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;
    private const long Week = 7 * Day;

    /// <summary>Ordinary, spring forward, fall back, across the turn of the year.</summary>
    private static readonly DateTime[] Starts =
    [
        new(2026, 6, 1), new(2026, 3, 23), new(2026, 10, 19), new(2026, 12, 21)
    ];

    private static readonly Gen<ShiftDefinition> GenShift =
        from index in Gen.Int[0, 999]
        from startMinutes in Gen.Int[0, 47].Select(q => q * 30)
        from lengthMinutes in Gen.Int[2, 28].Select(h => h * 30)     // 1 h .. 14 h
        from days in Gen.Int[1, 127]
        from crew in Gen.Int[0, 2]
        select new ShiftDefinition(
            $"s{index}",
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(startMinutes)),
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes((startMinutes + lengthMinutes) % 1440)),
            (WorkDays)days,
            $"crew{crew}");

    private static readonly Gen<ShiftPattern> GenPattern =
        GenShift.Array[0, 3].Select(shifts => new ShiftPattern("random",
            shifts.Select((s, i) => s with { Key = $"s{i}" }).ToList()));

    private static readonly Gen<WorkingTimeRules> GenRules =
        from state in Gen.Int[0, 15]
        from extended in Gen.Bool
        from sunday in Gen.Bool
        from holiday in Gen.Bool
        from boundary in Gen.Int[-6, 6]
        from rotation in Gen.Int[1, 6]
        select WorkingTimeRules.Statutory with
        {
            State = (GermanState)state,
            AllowExtendedDay = extended,
            SundayWorkAllowed = sunday,
            HolidayWorkAllowed = holiday,
            SundayBoundaryShift = TimeSpan.FromHours(boundary),
            SundayRotationWeeks = rotation
        };

    private static readonly Gen<WorkingTimeline> GenTimeline =
        from pattern in GenPattern
        from rules in GenRules
        from startIndex in Gen.Int[0, Starts.Length - 1]
        from absences in
            (from startHour in Gen.Int[0, 27 * 24]
             from length in Gen.Int[1, 72]
             select new AbsencePeriod(
                 Starts[startIndex].AddHours(startHour),
                 Starts[startIndex].AddHours(startHour + length),
                 AbsenceKind.Maintenance, "m")).Array[0, 3]
        select WorkingTimelineBuilder.Build(pattern, rules, absences, Starts[startIndex], Starts[startIndex].AddDays(28));

    [Fact]
    public void Windows_are_sorted_disjoint_and_inside_the_week() =>
        GenTimeline.Sample(timeline =>
        {
            long previousEnd = 0;
            foreach (var window in timeline.WeekWindows)
            {
                Assert.True(window.StartSeconds >= previousEnd, "windows overlap or are unsorted");
                Assert.True(window.EndSeconds > window.StartSeconds);
                Assert.True(window.EndSeconds <= Week);
                previousEnd = window.EndSeconds;
            }
        });

    [Fact]
    public void Windows_and_gaps_tile_the_week_exactly() =>
        GenTimeline.Sample(timeline =>
        {
            // The one case with nothing to tile is the unattended machine, which
            // has neither windows nor gaps by construction. A pattern the rules
            // emptied is *not* that case: it has a closed week to describe, and
            // returning early on it is what hid the bug this property exists for.
            if (timeline.Capacity.IsUnconstrained)
            {
                Assert.Empty(timeline.WeekWindows);
                Assert.Empty(timeline.WeekGaps);
                return;
            }

            var pieces = timeline.WeekWindows.Concat(timeline.WeekGaps).OrderBy(p => p.StartSeconds).ToList();
            long cursor = 0;
            foreach (var piece in pieces)
            {
                Assert.Equal(cursor, piece.StartSeconds);
                cursor = piece.EndSeconds;
            }

            Assert.Equal(Week, cursor);
        });

    [Fact]
    public void No_working_stretch_exceeds_six_hours_and_no_crew_day_exceeds_the_cap() =>
        GenTimeline.Sample(timeline =>
        {
            // Both limits are owed to a person, so they are measured on the crew
            // view. On the machine's side two crews handing over merge into one
            // open stretch that legitimately exceeds either limit - for the
            // machine, not for anybody working it.
            long dayCap = (long)timeline.Rules.DailyCap.TotalSeconds;
            long nightCap = (long)timeline.Rules.NightCap.TotalSeconds;
            long stretch = (long)timeline.Rules.MaxWorkWithoutBreak.TotalSeconds;

            foreach (var instance in timeline.CrewShifts)
                foreach (var span in instance.WorkingSpans)
                    Assert.True(span.DurationSeconds <= stretch,
                        $"{instance.Crew} works {span.DurationSeconds}s without a break; " + Describe(timeline));

            foreach (var group in timeline.CrewShifts.GroupBy(i => (i.Crew, DayIndex: i.StartSeconds / Day)))
            {
                long cap = group.Any(i => i.NightWork) ? Math.Min(dayCap, nightCap) : dayCap;
                long worked = group.Sum(i => i.WorkingSeconds);
                Assert.True(worked <= cap,
                    $"crew {group.Key.Crew} works {worked}s on day {group.Key.DayIndex}, cap {cap}s; " + Describe(timeline));
            }
        });

    [Fact]
    public void Every_crew_gets_its_rest_between_two_working_days() =>
        GenTimeline.Sample(timeline =>
        {
            // §5 is the most delicate code in the builder and the only section
            // that had no property at all, while docs/TESTING.md said it did.
            long rest = (long)timeline.Rules.MinimumRest.TotalSeconds;

            foreach (var crew in timeline.CrewShifts.GroupBy(i => i.Crew, StringComparer.Ordinal))
            {
                var ordered = crew.OrderBy(i => i.StartSeconds).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    // The pattern repeats, so the gap from the last shift of the
                    // week back to the first is as real as any other.
                    long previousEnd = i == 0 ? ordered[^1].EndSeconds - Week : ordered[i - 1].EndSeconds;
                    long gap = ordered[i].StartSeconds - previousEnd;
                    Assert.True(gap >= rest,
                        $"crew {crew.Key} gets {gap}s of rest before {ordered[i].ShiftKey} on {ordered[i].Day}; " + Describe(timeline));
                }
            }
        });

    [Fact]
    public void Sundays_are_never_worked_unless_allowed() =>
        GenTimeline.Sample(timeline =>
        {
            if (timeline.Rules.SundayWorkAllowed || timeline.Capacity.IsUnconstrained)
                return;

            long shift = (long)timeline.Rules.SundayBoundaryShift.TotalSeconds;
            Assert.DoesNotContain(timeline.WeekWindows, w =>
                (w.StartSeconds < 7 * Day + shift && w.EndSeconds > 6 * Day + shift) || w.StartSeconds < shift);

            // Every Sunday of the year stays free, and the count is the calendar's
            // rather than a constant - a year has 52 or 53 of them.
            Assert.False(timeline.WorksAnySunday);
            Assert.Equal(SundaysIn(timeline.From.Year), timeline.FreeSundaysPerYear);
            Assert.Equal(SundayRestStatus.Intact, timeline.SundayRest);
            Assert.False(timeline.ViolatesFreeSundays);
        });

    [Fact]
    public void A_pattern_with_no_working_time_left_is_closed_and_not_unconstrained() =>
        GenTimeline.Sample(timeline =>
        {
            if (timeline.WeekWindows.Count > 0)
                return;

            if (timeline.Pattern.IsContinuous)
            {
                Assert.True(timeline.Capacity.IsUnconstrained);
                Assert.Equal(7 * Day, timeline.WeeklyWorkingSeconds);
                return;
            }

            Assert.IsType<WeekCapacity.Closed>(timeline.Capacity);
            Assert.Equal(0, timeline.WeeklyWorkingSeconds);
            Assert.Equal(0, timeline.ToMachineCalendar(timeline.From).LongestPlacementSeconds);
            Assert.DoesNotContain(timeline.Segments(timeline.From, timeline.To), s => s.Kind == SegmentKind.Working);
        });

    [Fact]
    public void Exceptions_are_sorted_disjoint_and_inside_the_range() =>
        GenTimeline.Sample(timeline =>
        {
            var previousEnd = timeline.From;
            foreach (var exception in timeline.Exceptions)
            {
                Assert.True(exception.Start >= previousEnd);
                Assert.True(exception.End > exception.Start);
                Assert.True(exception.End <= timeline.To);
                Assert.True(exception.Kind is SegmentKind.Holiday or SegmentKind.Absence);
                Assert.NotEmpty(exception.Causes);
                previousEnd = exception.End;
            }
        });

    [Fact]
    public void Segments_always_tile_the_requested_range() =>
        GenTimeline.Sample(timeline =>
        {
            var from = timeline.From.AddDays(2).AddHours(5);
            var to = timeline.From.AddDays(19).AddHours(17);
            var segments = timeline.Segments(from, to);

            Assert.Equal(from, segments[0].Start);
            Assert.Equal(to, segments[^1].End);
            for (int i = 1; i < segments.Count; i++)
                Assert.Equal(segments[i - 1].End, segments[i].Start);
        });

    [Fact]
    public void Building_the_same_inputs_twice_gives_the_same_timeline() =>
        (from pattern in GenPattern
         from rules in GenRules
         select (pattern, rules)).Sample(input =>
        {
            var from = new DateTime(2026, 6, 1);
            var first = WorkingTimelineBuilder.Build(input.pattern, input.rules, [], from, from.AddDays(28));
            var second = WorkingTimelineBuilder.Build(input.pattern, input.rules, [], from, from.AddDays(28));

            Assert.Equal(first.Capacity, second.Capacity);
            Assert.Equal(first.WeekGaps, second.WeekGaps);
            Assert.Equal(first.CrewShifts, second.CrewShifts);
            Assert.Equal(first.Exceptions, second.Exceptions);
            Assert.Equal(first.Applications, second.Applications);
            Assert.Equal(first.WeeklyWorkingSeconds, second.WeeklyWorkingSeconds);
        });

    [Fact]
    public void A_week_of_segments_adds_up_to_the_weekly_total() =>
        (from pattern in GenPattern
         from sunday in Gen.Bool
         from boundary in Gen.Int[-6, 6]
         select (pattern, sunday, boundary)).Sample(input =>
        {
            // Holidays off and no absences, so the week is only the pattern.
            var rules = WorkingTimeRules.Statutory with
            {
                HolidayWorkAllowed = true,
                SundayWorkAllowed = input.sunday,
                SundayBoundaryShift = TimeSpan.FromHours(input.boundary)
            };
            var monday = new DateTime(2026, 6, 8);
            var timeline = WorkingTimelineBuilder.Build(input.pattern, rules, [], monday, monday.AddDays(28));
            if (timeline.Capacity.IsUnconstrained)
                return;

            long worked = timeline.Segments(monday, monday.AddDays(7))
                .Where(s => s.Kind == SegmentKind.Working)
                .Sum(s => (long)s.Duration.TotalSeconds);

            Assert.Equal(timeline.WeeklyWorkingSeconds, worked);
        });

    [Fact]
    public void The_engine_accepts_every_calendar_and_places_work_only_in_open_time() =>
        GenTimeline.Sample(timeline =>
        {
            var horizon = timeline.From.AddDays(1).AddHours(9);
            var calendar = timeline.ToMachineCalendar(horizon);
            var machine = calendar.ApplyTo(new MachineCapacity(1, "M"));

            long longest = calendar.LongestPlacementSeconds;
            if (longest < 15 * 60)
                return;   // a pattern with no usable window is a valid answer, just not schedulable

            long duration = Math.Min(longest, 5 * Hour);
            var jobs = Enumerable.Range(1, 3)
                .Select(i => new ProductionJob { Id = i, Reference = $"J{i}", Steps = [new JobStep(10, 1, duration)] })
                .ToList();
            var context = new SchedulingContext(jobs, [machine],
                new SchedulingParameters { DispatchRule = DispatchRule.Fifo, MultiStartRuns = 1, LocalSearchMaxSteps = 0 });

            var schedule = new SchedulingEngine().Run(context).Schedule;

            // Every placement must sit on Working segments of the annotated
            // timeline, pauses only on Break segments.
            foreach (var op in schedule.Operations)
            {
                var start = horizon.AddSeconds(op.StartSeconds);
                var end = horizon.AddSeconds(op.EndSeconds);
                if (end > timeline.To)
                    continue;   // past the materialised exceptions the engine only knows the pattern

                // Every closed stretch inside the placement is a pause: at most a
                // break long (a 30-minute hand-over gap between two crews counts
                // the same as a break - the machine simply waits), and never a
                // holiday or an absence, which are blackouts the engine may not
                // bridge.
                var covered = timeline.Segments(start, end);
                long working = covered.Where(s => s.Kind == SegmentKind.Working).Sum(s => (long)s.Duration.TotalSeconds);
                var closed = covered.Where(s => s.Kind != SegmentKind.Working).ToList();
                string detail = $"op [{start:ddd HH:mm}, {end:ddd HH:mm}) busy {op.BusySeconds} paused {op.PausedSeconds}; segments: " +
                    string.Join(" | ", covered.Select(s => $"{s.Kind}:{s.Label} {s.Start:ddd HH:mm}-{s.End:ddd HH:mm}"));

                Assert.True(op.BusySeconds == working, $"busy != working. {detail}");
                Assert.True(op.PausedSeconds == closed.Sum(s => (long)s.Duration.TotalSeconds), $"paused != closed. {detail}");
                Assert.True(closed.All(s => s.Duration.TotalSeconds <= calendar.MaxBridgeableGapSeconds), $"bridged a gap longer than a break. {detail}");
                Assert.True(!closed.Any(s => s.Kind is SegmentKind.Holiday or SegmentKind.Absence), $"bridged a blackout. {detail}");
            }
        });

    private static int SundaysIn(int year)
    {
        int count = 0;
        for (var date = new DateOnly(year, 1, 1); date.Year == year; date = date.AddDays(1))
            if (date.DayOfWeek == DayOfWeek.Sunday)
                count++;
        return count;
    }

    private static string Describe(WorkingTimeline timeline) =>
        "pattern: " + string.Join(" ", timeline.Pattern.Shifts.Select(s => $"{s.Key}={s.Start:HH\\:mm}-{s.End:HH\\:mm}/{(int)s.Days}/{s.Crew}")) +
        $"; sunday={timeline.Rules.SundayWorkAllowed} shift={timeline.Rules.SundayBoundaryShift} extended={timeline.Rules.AllowExtendedDay}";
}
