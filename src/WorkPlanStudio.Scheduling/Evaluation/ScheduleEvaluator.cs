using System.Diagnostics;

namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Scores a <see cref="Schedule"/>: rolls up the lateness / makespan / utilisation
/// KPIs and combines them into the single <see cref="ScheduleEvaluation.Penalty"/>
/// that the search optimises. Pure and deterministic — sums are taken in a fixed
/// (sorted) order so the double-valued penalty is reproducible.
/// <para>
/// This is the <i>reporting</i> half. The search never calls it: it compares
/// candidates through <see cref="ScheduleScore"/>, which reads only the three
/// numbers the penalty is built from. Everything here beyond those three exists
/// for the person looking at the result, and is computed once, for the schedule
/// that is kept.
/// </para>
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>The three objective numbers of <paramref name="schedule"/>, without the KPIs.</summary>
    public static ScheduleScore Score(Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        long totalTardiness = 0;
        int lateJobs = 0;
        foreach (var job in schedule.Jobs)
        {
            if (!job.IsLate)
                continue;
            lateJobs++;
            totalTardiness = checked(totalTardiness + job.TardinessSeconds);
        }

        return new ScheduleScore(schedule.MakespanSeconds, totalTardiness, lateJobs);
    }

    /// <summary>Scores <paramref name="schedule"/> against the <paramref name="context"/>'s parameters.</summary>
    public static ScheduleEvaluation Evaluate(Schedule schedule, SchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(context);

        long makespan = schedule.MakespanSeconds;
        int jobCount = schedule.Jobs.Count;

        long maxTardiness = 0;
        long totalFlow = 0;
        foreach (var job in schedule.Jobs)
        {
            long tardiness = job.TardinessSeconds;
            if (tardiness > maxTardiness) maxTardiness = tardiness;
            totalFlow = checked(totalFlow + job.FlowSeconds);
        }

        var score = Score(schedule);
        Debug.Assert(score.TotalTardinessSeconds >= 0, "total tardiness went negative — the timeline overflowed");

        double onTimeRate = jobCount == 0 ? 1.0 : (double)(jobCount - score.LateJobCount) / jobCount;
        double averageFlow = jobCount == 0 ? 0.0 : (double)totalFlow / jobCount;

        // Utilisation: busy ÷ (capacity × open time up to the makespan) for each
        // work center used. Open time is the calendar's, so a one-shift machine
        // that ran every hour it was staffed reads 100 %, not 33 %.
        var busyByWorkCenter = new Dictionary<int, long>();
        foreach (var op in schedule.Operations)
            busyByWorkCenter[op.WorkCenterId] =
                busyByWorkCenter.GetValueOrDefault(op.WorkCenterId) + op.BusySeconds;

        var utilization = new Dictionary<int, double>(busyByWorkCenter.Count);
        foreach (var (workCenterId, busy) in busyByWorkCenter)
        {
            long open = context.Machines.TryGetValue(workCenterId, out var machine)
                ? machine.OpenSecondsWithin(makespan)
                : makespan;
            double available = (double)context.CapacityOf(workCenterId) * open;

            // Not clamped to 1. A utilisation above 1 is proof that the capacity
            // invariant broke, and capping it reports the broken schedule as a
            // machine that ran flat out — which is the one self-check this KPI is
            // worth having.
            double raw = available <= 0 ? 0.0 : busy / available;
            Debug.Assert(raw <= 1.0 + 1e-9, $"work center {workCenterId} is over capacity at {raw:F3}");
            utilization[workCenterId] = raw;
        }

        // Average in a fixed key order so the result is bit-stable.
        double averageUtilization = 0.0;
        if (utilization.Count > 0)
        {
            double sum = 0.0;
            foreach (var workCenterId in utilization.Keys.Order())
                sum += utilization[workCenterId];
            averageUtilization = sum / utilization.Count;
        }

        return new ScheduleEvaluation
        {
            MakespanSeconds = makespan,
            TotalTardinessSeconds = score.TotalTardinessSeconds,
            MaxTardinessSeconds = maxTardiness,
            LateJobCount = score.LateJobCount,
            JobCount = jobCount,
            OnTimeRate = onTimeRate,
            AverageFlowSeconds = averageFlow,
            UtilizationByWorkCenter = utilization,
            AverageUtilization = averageUtilization,
            Penalty = score.Penalty(context.Parameters)
        };
    }
}
