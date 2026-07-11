namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Turns a completed <see cref="SchedulingResult"/> into a structured, deterministic
/// <see cref="ScheduleExplanation"/>: summary KPIs, the bottleneck work center, the
/// worst late jobs with the resource each waited on, and one computed recommendation
/// (found by quickly re-dispatching the other rules). Pure and reproducible — the
/// same inputs always yield the same explanation, so it can back both a rule-based
/// narrator and an optional AI one without any nondeterminism leaking in.
/// </summary>
public static class ScheduleExplainer
{
    /// <summary>Upper bound on how many late jobs are listed individually.</summary>
    public const int MaxLateJobs = 5;

    // Probe budget for the recommendation: small on purpose. We only ever suggest a
    // switch when a *capped* alternative already beats the current (full-budget)
    // result, which keeps the advice conservative and the extra work cheap.
    private const int ProbeMultiStart = 4;
    private const int ProbeLocalSearch = 400;

    /// <summary>Builds the explanation for <paramref name="result"/> under <paramref name="context"/>.</summary>
    public static ScheduleExplanation Explain(SchedulingContext context, SchedulingResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        var eval = result.Evaluation;
        var summary = new ScheduleSummary(
            eval.JobCount,
            eval.JobCount - eval.LateJobCount,
            eval.MakespanSeconds,
            eval.TotalTardinessSeconds,
            eval.AverageUtilization);

        return new ScheduleExplanation(
            summary,
            FindBottleneck(context, result),
            FindLateJobs(context, result),
            Recommend(context, result));
    }

    /// <summary>The work center with the highest utilisation (ties broken by id).</summary>
    private static BottleneckFinding? FindBottleneck(SchedulingContext context, SchedulingResult result)
    {
        var utilization = result.Evaluation.UtilizationByWorkCenter;
        if (utilization.Count == 0)
            return null;

        int bestId = -1;
        double bestUtil = double.NegativeInfinity;
        foreach (var id in utilization.Keys.OrderBy(k => k))
        {
            if (utilization[id] > bestUtil)
            {
                bestUtil = utilization[id];
                bestId = id;
            }
        }

        int operationCount = result.Schedule.Operations.Count(o => o.WorkCenterId == bestId);
        string name = context.Machines.TryGetValue(bestId, out var machine)
            ? machine.Name
            : bestId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new BottleneckFinding(bestId, name, bestUtil, operationCount);
    }

    /// <summary>The most tardy jobs, each with the resource it queued on longest.</summary>
    private static IReadOnlyList<LateJobFinding> FindLateJobs(SchedulingContext context, SchedulingResult result)
    {
        var late = result.Schedule.Jobs
            .Where(j => j.IsLate)
            .OrderByDescending(j => j.TardinessSeconds)
            .ThenBy(j => j.JobId)
            .Take(MaxLateJobs);

        var findings = new List<LateJobFinding>();
        foreach (var job in late)
        {
            // Walk the job's operations in order; time between one step finishing and
            // the next starting is time spent queuing for a busy work center.
            var operations = result.Schedule.Operations
                .Where(o => o.JobId == job.JobId)
                .OrderBy(o => o.StepNumber);

            long readyAt = job.ReleaseSeconds;
            long totalWait = 0;
            long worstWait = 0;
            int worstWorkCenter = -1;
            foreach (var op in operations)
            {
                long wait = op.StartSeconds - readyAt;
                if (wait > 0)
                {
                    totalWait += wait;
                    if (wait > worstWait)
                    {
                        worstWait = wait;
                        worstWorkCenter = op.WorkCenterId;
                    }
                }
                readyAt = op.EndSeconds;
            }

            string? blocking = worstWorkCenter >= 0 && context.Machines.TryGetValue(worstWorkCenter, out var machine)
                ? machine.Name
                : null;

            findings.Add(new LateJobFinding(job.JobId, job.Reference, job.TardinessSeconds, totalWait, blocking));
        }

        return findings;
    }

    /// <summary>One computed suggestion: keep the rule, switch it, or accept the result.</summary>
    private static ScheduleRecommendation Recommend(SchedulingContext context, SchedulingResult result)
    {
        var currentRule = context.Parameters.DispatchRule;
        long currentTardiness = result.Evaluation.TotalTardinessSeconds;

        if (currentTardiness == 0)
            return new ScheduleRecommendation(RecommendationKind.AlreadyOnTime, currentRule, null, 0, 0);

        // Fair what-if: due dates depend on the due-date rule, not the dispatch rule,
        // so swapping only the dispatch rule isolates its effect. Cap the budget so
        // the probe stays cheap even when the real run uses a large search.
        var probeParameters = context.Parameters with
        {
            MultiStartRuns = Math.Min(context.Parameters.MultiStartRuns, ProbeMultiStart),
            LocalSearchMaxSteps = Math.Min(context.Parameters.LocalSearchMaxSteps, ProbeLocalSearch)
        };
        var machines = context.Machines.Values.ToList();
        var engine = new SchedulingEngine();

        DispatchRule? bestRule = null;
        long bestTardiness = currentTardiness;
        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            if (rule == currentRule)
                continue;

            var probe = new SchedulingContext(context.Jobs, machines, probeParameters with { DispatchRule = rule });
            long tardiness = engine.Run(probe).Evaluation.TotalTardinessSeconds;
            if (tardiness < bestTardiness)
            {
                bestTardiness = tardiness;
                bestRule = rule;
            }
        }

        return bestRule is null
            ? new ScheduleRecommendation(RecommendationKind.NoImprovementFound, currentRule, null, currentTardiness, currentTardiness)
            : new ScheduleRecommendation(RecommendationKind.SwitchDispatchRule, currentRule, bestRule, currentTardiness, bestTardiness);
    }
}
