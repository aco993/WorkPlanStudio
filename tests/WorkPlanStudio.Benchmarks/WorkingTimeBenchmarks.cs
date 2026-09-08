using BenchmarkDotNet.Attributes;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Benchmarks;

/// <summary>
/// The working-time library: holidays for a year, and a timeline (shifts →
/// ArbZG rules → machine calendar) over the app's 400-day lookahead, which is
/// what it builds once per work center before every scheduling run.
/// </summary>
[MemoryDiagnoser]
public class WorkingTimeBenchmarks
{
    private static readonly DateTime From = new(2026, 6, 1);
    private static readonly DateTime To = From.AddDays(400);
    private static readonly DateTime Horizon = From.AddHours(6);

    [Benchmark(Description = "holidays: all 16 states, one year")]
    public int HolidaysForAllStates()
    {
        int count = 0;
        foreach (var state in Enum.GetValues<GermanState>())
            count += GermanHolidays.ForYear(2026, state, includePartial: true).Count;
        return count;
    }

    [Benchmark(Baseline = true, Description = "timeline: three-shift pattern, 400 days")]
    public MachineCalendar ThreeShiftTimeline() =>
        WorkingTimelineBuilder.Build(ShiftPatterns.ThreeShift, WorkingTimeRules.Statutory, [], From, To).ToMachineCalendar(Horizon);

    [Benchmark(Description = "timeline: one-shift pattern with an absence, 400 days")]
    public MachineCalendar OneShiftWithAbsence() =>
        WorkingTimelineBuilder.Build(
            ShiftPatterns.OneShift, WorkingTimeRules.Statutory,
            [new AbsencePeriod(From.AddDays(1).AddHours(7), From.AddDays(1).AddHours(15), AbsenceKind.Maintenance, "service")],
            From, To).ToMachineCalendar(Horizon);
}
