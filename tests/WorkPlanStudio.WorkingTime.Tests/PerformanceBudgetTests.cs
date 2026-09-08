using System.Diagnostics;

namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// Tripwires, not benchmarks: the app builds one timeline per work center over
/// a 400-day lookahead before every scheduling run, so that build has to stay
/// in the low milliseconds. The bounds are an order of magnitude above a
/// laptop's measurement; a failure means something went quadratic.
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
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"timeline took {stopwatch.ElapsedMilliseconds} ms, budget 500 ms");
    }

    [Fact]
    public void Holidays_for_every_state_and_a_decade_compute_within_their_budget()
    {
        _ = GermanHolidays.ForYear(2026, GermanState.NW);

        var stopwatch = Stopwatch.StartNew();
        int count = 0;
        for (int year = 2020; year < 2030; year++)
            foreach (var state in Enum.GetValues<GermanState>())
                count += GermanHolidays.ForYear(year, state, includePartial: true).Count;
        stopwatch.Stop();

        Assert.True(count > 1_000, "expected more than a thousand holiday entries");
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"holidays took {stopwatch.ElapsedMilliseconds} ms, budget 500 ms");
    }
}
