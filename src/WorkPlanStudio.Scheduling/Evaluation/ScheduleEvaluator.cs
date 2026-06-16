namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Scores a <see cref="Schedule"/>: rolls up the lateness / makespan / utilisation
/// KPIs and combines them into the single <see cref="ScheduleEvaluation.Penalty"/>
/// that the search optimises. Pure and deterministic — sums are taken in a fixed
/// (sorted) order so the double-valued penalty is reproducible.
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>Scores <paramref name="schedule"/> against the <paramref name="context"/>'s parameters.</summary>
    public static ScheduleEvaluation Evaluate(Schedule schedule, SchedulingContext context)
    {
        long makespan = schedule.MakespanSeconds;
        int jobCount = schedule.Jobs.Count;

        long totalTardiness = 0;
        long maxTardiness = 0;
        long totalFlow = 0;
        int lateJobs = 0;

        foreach (var job in schedule.Jobs)
        {
            long tardiness = job.TardinessSeconds;
            totalTardiness += tardiness;
            if (tardiness > maxTardiness) maxTardiness = tardiness;
            if (job.IsLate) lateJobs++;
            totalFlow += job.FlowSeconds;
        }

        double onTimeRate = jobCount == 0 ? 1.0 : (double)(jobCount - lateJobs) / jobCount;
        double averageFlow = jobCount == 0 ? 0.0 : (double)totalFlow / jobCount;

        // Utilisation: busy ÷ (capacity × makespan) for each work center used.
        var busyByWorkCenter = new Dictionary<int, long>();
        foreach (var op in schedule.Operations)
            busyByWorkCenter[op.WorkCenterId] =
                busyByWorkCenter.GetValueOrDefault(op.WorkCenterId) + op.DurationSeconds;

        var utilization = new Dictionary<int, double>(busyByWorkCenter.Count);
        foreach (var (workCenterId, busy) in busyByWorkCenter)
        {
            double available = (double)context.CapacityOf(workCenterId) * makespan;
            utilization[workCenterId] = available <= 0 ? 0.0 : busy / available;
        }

        // Average in a fixed key order so the result is bit-stable.
        double averageUtilization = 0.0;
        if (utilization.Count > 0)
        {
            double sum = 0.0;
            foreach (var workCenterId in utilization.Keys.OrderBy(k => k))
                sum += utilization[workCenterId];
            averageUtilization = sum / utilization.Count;
        }

        var p = context.Parameters;
        double penalty =
            p.MakespanWeight * (makespan / 3600.0) +
            p.TardinessWeight * (totalTardiness / 3600.0) +
            p.LatePenalty * lateJobs;

        return new ScheduleEvaluation
        {
            MakespanSeconds = makespan,
            TotalTardinessSeconds = totalTardiness,
            MaxTardinessSeconds = maxTardiness,
            LateJobCount = lateJobs,
            JobCount = jobCount,
            OnTimeRate = onTimeRate,
            AverageFlowSeconds = averageFlow,
            UtilizationByWorkCenter = utilization,
            AverageUtilization = averageUtilization,
            Penalty = penalty
        };
    }
}
