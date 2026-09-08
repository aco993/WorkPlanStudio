using CsCheck;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// Invariants of the working-time model over random shift patterns, rules and
/// absences: the statutory limits hold in every week the builder produces, and
/// what it hands to the engine is always a valid, schedulable calendar.
/// </summary>
public class WorkingTimePropertyTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;
    private const long Week = 7 * Day;

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
        from boundary in Gen.Int[0, 6]
        select WorkingTimeRules.Statutory with
        {
            State = (GermanState)state,
            AllowExtendedDay = extended,
            SundayWorkAllowed = sunday,
            HolidayWorkAllowed = holiday,
            SundayBoundaryShift = TimeSpan.FromHours(boundary)
        };

    private static readonly Gen<AbsencePeriod[]> GenAbsences =
        (from startHour in Gen.Int[0, 27 * 24]
         from length in Gen.Int[1, 72]
         select new AbsencePeriod(
             new DateTime(2026, 6, 1).AddHours(startHour),
             new DateTime(2026, 6, 1).AddHours(startHour + length),
             AbsenceKind.Maintenance, "m")).Array[0, 3];

    private static readonly Gen<WorkingTimeline> GenTimeline =
        from pattern in GenPattern
        from rules in GenRules
        from absences in GenAbsences
        select WorkingTimelineBuilder.Build(pattern, rules, absences, new DateTime(2026, 6, 1), new DateTime(2026, 6, 29));

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
            if (timeline.WeekWindows.Count == 0)
                return;

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
    public void No_working_stretch_exceeds_six_hours_and_no_day_exceeds_the_cap() =>
        GenTimeline.Sample(timeline =>
        {
            // Both limits are per crew. Windows where two crews overlap or hand
            // over merge into one open stretch (label "a+b") and legitimately
            // exceed either limit for the machine — not for any one person.
            long cap = (long)timeline.Rules.DailyCap.TotalSeconds;
            var singleCrew = timeline.WeekWindows.Where(w => !w.Label.Contains('+', StringComparison.Ordinal)).ToList();

            Assert.All(singleCrew, w => Assert.True(w.DurationSeconds <= 6 * Hour, "a stretch over six hours without a break"));
            foreach (var group in singleCrew.GroupBy(w => (w.Label, w.Day)))
                Assert.True(group.Sum(w => w.DurationSeconds) <= cap + 1,
                    $"shift {group.Key.Label} works more than the cap on day {group.Key.Day}: " +
                    string.Join(",", group.Select(w => $"{w.StartSeconds}-{w.EndSeconds}")) +
                    "; pattern: " + string.Join(" ", timeline.Pattern.Shifts.Select(s => $"{s.Key}={s.Start:HH\\:mm}-{s.End:HH\\:mm}/{(int)s.Days}/{s.Crew}")) +
                    $"; sunday={timeline.Rules.SundayWorkAllowed} shift={timeline.Rules.SundayBoundaryShift}");
        });

    [Fact]
    public void Sundays_are_never_worked_unless_allowed() =>
        GenTimeline.Sample(timeline =>
        {
            if (timeline.Rules.SundayWorkAllowed || timeline.Pattern.IsContinuous)
                return;

            long shift = (long)timeline.Rules.SundayBoundaryShift.TotalSeconds;
            Assert.DoesNotContain(timeline.WeekWindows, w =>
                (w.StartSeconds < 7 * Day + shift && w.EndSeconds > 6 * Day + shift) || w.StartSeconds < shift);
            Assert.Equal(52, timeline.FreeSundaysPerYear);
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
                previousEnd = exception.End;
            }
        });

    [Fact]
    public void Segments_always_tile_the_requested_range() =>
        GenTimeline.Sample(timeline =>
        {
            var from = new DateTime(2026, 6, 3, 5, 0, 0);
            var to = new DateTime(2026, 6, 20, 17, 0, 0);
            var segments = timeline.Segments(from, to);

            Assert.Equal(from, segments[0].Start);
            Assert.Equal(to, segments[^1].End);
            for (int i = 1; i < segments.Count; i++)
                Assert.Equal(segments[i - 1].End, segments[i].Start);
        });

    [Fact]
    public void The_engine_accepts_every_calendar_and_places_work_only_in_open_time() =>
        GenTimeline.Sample(timeline =>
        {
            var horizon = new DateTime(2026, 6, 2, 9, 0, 0);
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
}
