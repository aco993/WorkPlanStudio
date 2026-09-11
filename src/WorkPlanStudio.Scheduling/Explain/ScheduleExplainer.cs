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
    /// <param name="context">The instance the result was produced from.</param>
    /// <param name="result">The completed run.</param>
    /// <param name="probeAlternativeRules">
    /// Whether to re-dispatch the other rules to look for a better one. It is the
    /// only expensive part of an explanation — five capped searches — so a caller
    /// that has to stay responsive can turn it off and get
    /// <see cref="RecommendationKind.NotProbed"/> instead.
    /// </param>
    public static ScheduleExplanation Explain(
        SchedulingContext context, SchedulingResult result, bool probeAlternativeRules = true)
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
            Recommend(context, result, probeAlternativeRules));
    }

    /// <summary>The work center with the highest utilisation (ties broken by id).</summary>
    private static BottleneckFinding? FindBottleneck(SchedulingContext context, SchedulingResult result)
    {
        var utilization = result.Evaluation.UtilizationByWorkCenter;
        if (utilization.Count == 0)
            return null;

        int bestId = -1;
        double bestUtil = double.NegativeInfinity;
        foreach (var id in utilization.Keys.Order())
        {
            if (utilization[id] > bestUtil)
            {
                bestUtil = utilization[id];
                bestId = id;
            }
        }

        int operationCount = 0;
        foreach (var op in result.Schedule.Operations)
        {
            if (op.WorkCenterId == bestId)
                operationCount++;
        }

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
    /// <remarks>
    /// Ranked by <see cref="ScheduleEvaluation.Penalty"/> — the objective the engine
    /// actually minimises — not by total tardiness. Ranking by tardiness alone is
    /// blind to the flat per-late-job penalty, so with the shipped weights (one late
    /// job is worth ten hours of tardiness) it will happily trade one badly late job
    /// for three barely late ones and report it as an improvement. Measured, that
    /// advice made a four-job schedule 2.3× worse by the engine's own score.
    /// </remarks>
    private static ScheduleRecommendation Recommend(
        SchedulingContext context, SchedulingResult result, bool probeAlternativeRules)
    {
        var currentRule = context.Parameters.DispatchRule;
        long currentTardiness = result.Evaluation.TotalTardinessSeconds;

        if (currentTardiness == 0)
            return new ScheduleRecommendation(RecommendationKind.AlreadyOnTime, currentRule, null, 0, 0);

        if (!probeAlternativeRules)
            return new ScheduleRecommendation(RecommendationKind.NotProbed, currentRule, null, currentTardiness, currentTardiness);

        // Fair what-if: due dates depend on the due-date rule, not the dispatch rule,
        // so swapping only the dispatch rule isolates its effect. Cap the budget so
        // the probe stays cheap even when the real run uses a large search.
        var probeParameters = context.Parameters with
        {
            MultiStartRuns = Math.Min(context.Parameters.MultiStartRuns, ProbeMultiStart),
            LocalSearchMaxSteps = Math.Min(context.Parameters.LocalSearchMaxSteps, ProbeLocalSearch)
        };

        // One set of target dates and one validated model for all five probes. Each
        // probe used to rebuild a SchedulingContext, which re-ran every input check,
        // and then computed the equivalent rules again from five more of them.
        var dueByJob = result.DueByJob;
        var engine = new SchedulingEngine();

        DispatchRule? bestRule = null;
        double bestPenalty = result.Evaluation.Penalty;
        long projectedTardiness = currentTardiness;
        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            if (rule == currentRule)
                continue;

            var probe = context.WithParameters(probeParameters with { DispatchRule = rule });
            var outcome = engine.Search(probe, dueByJob, CancellationToken.None);
            if (outcome.Evaluation.Penalty < bestPenalty)
            {
                bestPenalty = outcome.Evaluation.Penalty;
                projectedTardiness = outcome.Evaluation.TotalTardinessSeconds;
                bestRule = rule;
            }
        }

        return bestRule is null
            ? new ScheduleRecommendation(RecommendationKind.NoImprovementFound, currentRule, null, currentTardiness, currentTardiness)
            : new ScheduleRecommendation(RecommendationKind.SwitchDispatchRule, currentRule, bestRule, currentTardiness, projectedTardiness);
    }
}
