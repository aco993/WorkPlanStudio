using System.Diagnostics;

namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// Tripwires, not benchmarks: the app builds one timeline per work center over a
/// 400-day lookahead before every scheduling run and renders the segments of one
/// per Gantt, so both have to stay in the low milliseconds.
/// <para>
/// The bounds are roughly twenty times a laptop's measurement. That is loose
/// enough to survive a slow shared runner and tight enough to fail when
/// something goes quadratic — the previous 500 ms against a 10 µs measurement
/// could not have failed for any reason short of a hang.
/// </para>
/// </summary>
public class PerformanceBudgetTests
{
    private static readonly DateTime From = new(2026, 6, 1);

    [Fact]
    public void A_three_shift_timeline_over_400_days_builds_within_its_budget()
    {
        var to = From.AddDays(400);
        _ = WorkingTimelineBuilder.Build(ShiftPatterns.ThreeShift, WorkingTimeRules.Statutory, [], From, to);   // warm up

        var stopwatch = Stopwatch.StartNew();
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.ThreeShift, WorkingTimeRules.Statutory, [], From, to);
        var calendar = timeline.ToMachineCalendar(From.AddHours(6));
        stopwatch.Stop();

        Assert.NotEmpty(calendar.Windows);
        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"timeline took {stopwatch.ElapsedMilliseconds} ms, budget 50 ms");
    }

    [Fact]
    public void Segments_over_the_whole_materialised_range_stay_linear()
    {
        // The exception overlay used to rescan every exception for every segment.
        // Over five years that is ~8 700 segments against 56 exceptions, and the
        // scan had no early exit — the shape this budget exists to catch.
        var to = From.Add(WorkingTimelineBuilder.MaxRange);
        var absences = Enumerable.Range(0, 20)
            .Select(i => new AbsencePeriod(From.AddDays(30 * i), From.AddDays(30 * i + 1), AbsenceKind.Maintenance, $"m{i}"))
            .ToList();
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.ThreeShift, WorkingTimeRules.Statutory, absences, From, to);
        _ = timeline.Segments(From, to);   // warm up

        var stopwatch = Stopwatch.StartNew();
        var segments = timeline.Segments(From, to);
        stopwatch.Stop();

        Assert.True(segments.Count > 5_000, $"expected a segment per shift piece, got {segments.Count}");
        Assert.True(stopwatch.ElapsedMilliseconds < 150, $"segments took {stopwatch.ElapsedMilliseconds} ms, budget 150 ms");
    }

    [Fact]
    public void The_compliance_evaluation_over_400_days_stays_within_its_budget()
    {
        var to = From.AddDays(400);
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.ThreeShift, WorkingTimeRules.Statutory, [], From, to);
        _ = WorkingTimelineBuilder.Build(ShiftPatterns.ThreeShift, WorkingTimeRules.Statutory, [], From, to).Compliance;

        var stopwatch = Stopwatch.StartNew();
        var compliance = timeline.Compliance;
        stopwatch.Stop();

        Assert.NotEmpty(compliance.Days);
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"evaluation took {stopwatch.ElapsedMilliseconds} ms, budget 100 ms");
    }

    [Fact]
    public void Holidays_for_every_state_and_a_decade_compute_within_their_budget()
    {
        // Years no other test touches, so this measures the computation rather
        // than the cache the app actually benefits from.
        _ = GermanHolidays.ForYear(2149, GermanState.NW);

        var stopwatch = Stopwatch.StartNew();
        int count = 0;
        for (int year = 2150; year < 2160; year++)
            foreach (var state in Enum.GetValues<GermanState>())
                count += GermanHolidays.ForYear(year, state, includePartial: true).Count;
        stopwatch.Stop();

        Assert.True(count > 1_000, "expected more than a thousand holiday entries");
        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"holidays took {stopwatch.ElapsedMilliseconds} ms, budget 50 ms");
    }

    [Fact]
    public void A_cached_year_costs_nothing_to_ask_for_again()
    {
        for (int year = 2160; year < 2170; year++)
            foreach (var state in Enum.GetValues<GermanState>())
                _ = GermanHolidays.ForYear(year, state);

        var stopwatch = Stopwatch.StartNew();
        for (int repeat = 0; repeat < 100; repeat++)
            for (int year = 2160; year < 2170; year++)
                foreach (var state in Enum.GetValues<GermanState>())
                    _ = GermanHolidays.ForYear(year, state);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 50,
            $"16 000 cached lookups took {stopwatch.ElapsedMilliseconds} ms, budget 50 ms");
    }
}
