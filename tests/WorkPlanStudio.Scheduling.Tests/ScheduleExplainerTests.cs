namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Tests the deterministic explanation layer: it must summarise the run, point at
/// the real bottleneck, name the late jobs and the resource they queued on, and
/// only recommend a dispatch-rule switch when one actually helps.
/// </summary>
public class ScheduleExplainerTests
{
    // One machine, one long+loose job and one short+urgent job. Longest-processing
    // -time puts the long job first, so the urgent one finishes late — but shortest
    // -processing-time would clear both on time. A crisp, hand-checkable case.
    private static SchedulingContext LateByRuleChoice() => Context(
        new SchedulingParameters
        {
            DispatchRule = DispatchRule.LongestProcessingTime,
            DueDateRule = DueDateRule.Explicit,
            MultiStartRuns = 1,
            LocalSearchMaxSteps = 0
        },
        new[] { Machine(1) },
        DueAt(1, 100_000, Step(10, 1, 1000)),   // long, loose
        DueAt(2, 200, Step(10, 1, 100)));         // short, urgent

    private static ScheduleExplanation Explain(SchedulingContext ctx) =>
        ScheduleExplainer.Explain(ctx, new SchedulingEngine().Run(ctx));

    [Fact]
    public void Summary_mirrors_the_evaluation()
    {
        var explanation = Explain(LateByRuleChoice());

        Assert.Equal(2, explanation.Summary.JobCount);
        Assert.Equal(1, explanation.Summary.OnTimeCount);
        Assert.Equal(900, explanation.Summary.TotalTardinessSeconds);   // job 2 late by 900 s
    }

    [Fact]
    public void The_busiest_work_center_is_flagged_as_the_bottleneck()
    {
        var explanation = Explain(LateByRuleChoice());

        Assert.NotNull(explanation.Bottleneck);
        Assert.Equal(1, explanation.Bottleneck!.WorkCenterId);
        Assert.Equal("WC-1", explanation.Bottleneck.WorkCenterName);
        Assert.Equal(2, explanation.Bottleneck.OperationCount);
        Assert.Equal(1.0, explanation.Bottleneck.Utilization, 3);       // single machine, no idle gaps
    }

    [Fact]
    public void Late_jobs_are_listed_worst_first_with_the_resource_they_waited_on()
    {
        var explanation = Explain(LateByRuleChoice());

        var late = Assert.Single(explanation.LateJobs);
        Assert.Equal("J2", late.JobReference);
        Assert.Equal(900, late.TardinessSeconds);
        Assert.Equal(1000, late.QueueWaitSeconds);                      // waited behind the long job
        Assert.Equal("WC-1", late.BlockingWorkCenterName);
    }

    [Fact]
    public void Recommends_switching_to_a_rule_that_actually_helps()
    {
        var explanation = Explain(LateByRuleChoice());

        Assert.Equal(RecommendationKind.SwitchDispatchRule, explanation.Recommendation.Kind);
        Assert.Equal(DispatchRule.LongestProcessingTime, explanation.Recommendation.CurrentRule);
        Assert.Equal(DispatchRule.ShortestProcessingTime, explanation.Recommendation.SuggestedRule);
        Assert.Equal(900, explanation.Recommendation.CurrentTardinessSeconds);
        Assert.Equal(0, explanation.Recommendation.ProjectedTardinessSeconds);
    }

    [Fact]
    public void An_all_on_time_schedule_has_no_late_jobs_and_needs_no_change()
    {
        var ctx = Context(
            RuleOnly(DispatchRule.EarliestDueDate),
            new[] { Machine(1) },
            DueAt(1, 100_000, Step(10, 1, 100)));

        var explanation = Explain(ctx);

        Assert.Empty(explanation.LateJobs);
        Assert.Equal(RecommendationKind.AlreadyOnTime, explanation.Recommendation.Kind);
        Assert.Null(explanation.Recommendation.SuggestedRule);
    }

    [Fact]
    public void A_job_late_purely_from_a_tight_target_has_no_blocking_work_center()
    {
        // The job never queues (it runs immediately), it is just due before it can
        // physically finish — so there is no resource to blame.
        var ctx = Context(
            new SchedulingParameters { DueDateRule = DueDateRule.Explicit, MultiStartRuns = 1, LocalSearchMaxSteps = 0 },
            new[] { Machine(1) },
            DueAt(1, 50, Step(10, 1, 100)));

        var late = Assert.Single(Explain(ctx).LateJobs);

        Assert.Equal(0, late.QueueWaitSeconds);
        Assert.Null(late.BlockingWorkCenterName);
    }

    /// <summary>
    /// The fixture the old ranking got wrong. With the shipped weights a late job
    /// costs 100 and an hour of tardiness costs 10, so one late job is worth ten
    /// hours of tardiness — and shortest-processing-time here trades one job that
    /// is three hours late for three that are barely late. By total tardiness that
    /// reads as 10 800 s down to 300 s and looks like a large win; by the penalty
    /// the engine minimises it is 133.028 up to 303.861, a schedule 2.3× worse.
    /// </summary>
    [Fact]
    public void A_rule_that_cuts_tardiness_but_raises_the_penalty_is_not_recommended()
    {
        var context = Context(
            new SchedulingParameters
            {
                DispatchRule = DispatchRule.LongestProcessingTime,
                DueDateRule = DueDateRule.Explicit,
                MultiStartRuns = 1,
                LocalSearchMaxSteps = 0
            },
            new[] { Machine(1) },
            DueAt(1, 3_600, Step(10, 1, 3_600)),
            DueAt(2, 7_200, Step(10, 1, 3_600)),
            DueAt(3, 10_800, Step(10, 1, 3_600)),
            DueAt(4, 100, Step(10, 1, 100)));

        var result = new SchedulingEngine().Run(context);
        var explanation = ScheduleExplainer.Explain(context, result);

        // The current schedule: one job three hours late, penalty 133.028.
        Assert.Equal(10_800, result.Evaluation.TotalTardinessSeconds);
        Assert.Equal(1, result.Evaluation.LateJobCount);

        // Shortest-processing-time would cut tardiness to 300 s and still be worse.
        var alternative = context.WithParameters(context.Parameters with { DispatchRule = DispatchRule.ShortestProcessingTime });
        var alternativeResult = new SchedulingEngine().Run(alternative);
        Assert.True(alternativeResult.Evaluation.TotalTardinessSeconds < result.Evaluation.TotalTardinessSeconds);
        Assert.True(alternativeResult.Evaluation.Penalty > result.Evaluation.Penalty);

        Assert.NotEqual(DispatchRule.ShortestProcessingTime, explanation.Recommendation.SuggestedRule);
    }

    /// <summary>
    /// Whatever it does recommend, following the advice must actually help — by
    /// the objective, on every instance the suite generates.
    /// </summary>
    [Fact]
    public void A_recommended_rule_always_lowers_the_penalty()
    {
        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            var context = SearchTests.MediumScenario(rule);
            var result = new SchedulingEngine().Run(context);
            var recommendation = ScheduleExplainer.Explain(context, result).Recommendation;

            if (recommendation.SuggestedRule is not { } suggested)
                continue;

            var probe = context.WithParameters(context.Parameters with { DispatchRule = suggested });
            double suggestedPenalty = new SchedulingEngine().Run(probe).Evaluation.Penalty;

            Assert.True(suggestedPenalty < result.Evaluation.Penalty,
                $"from {rule}, the explainer suggested {suggested}, which scores {suggestedPenalty} against {result.Evaluation.Penalty}");
        }
    }

    /// <summary>
    /// The probe is five more searches; a caller that cannot spend them must be
    /// able to say so and still get the rest of the explanation.
    /// </summary>
    [Fact]
    public void The_rule_probe_can_be_skipped()
    {
        var context = LateByRuleChoice();
        var result = new SchedulingEngine().Run(context);

        var explanation = ScheduleExplainer.Explain(context, result, probeAlternativeRules: false);

        Assert.Equal(RecommendationKind.NotProbed, explanation.Recommendation.Kind);
        Assert.Null(explanation.Recommendation.SuggestedRule);
        Assert.NotNull(explanation.Bottleneck);
        Assert.Single(explanation.LateJobs);
    }

    /// <summary>
    /// More than <see cref="ScheduleExplainer.MaxLateJobs"/> late jobs: exactly
    /// five findings, worst first, ties broken by job id.
    /// </summary>
    [Fact]
    public void Late_jobs_are_truncated_worst_first()
    {
        var jobs = Enumerable.Range(1, 9)
            .Select(i => DueAt(i, 0, Step(10, 1, 100 * i)))
            .ToArray();
        var context = Context(
            new SchedulingParameters { DueDateRule = DueDateRule.Explicit, DispatchRule = DispatchRule.ShortestProcessingTime, MultiStartRuns = 1, LocalSearchMaxSteps = 0 },
            new[] { Machine(1) },
            jobs);

        var explanation = ScheduleExplainer.Explain(context, new SchedulingEngine().Run(context), probeAlternativeRules: false);

        Assert.Equal(ScheduleExplainer.MaxLateJobs, explanation.LateJobs.Count);
        var tardiness = explanation.LateJobs.Select(j => j.TardinessSeconds).ToArray();
        Assert.Equal(tardiness.OrderByDescending(t => t), tardiness);
    }

    [Fact]
    public void The_explanation_is_deterministic()
    {
        var a = Explain(LateByRuleChoice());
        var b = Explain(LateByRuleChoice());

        Assert.Equal(a.Summary, b.Summary);
        Assert.Equal(a.Bottleneck, b.Bottleneck);
        Assert.Equal(a.Recommendation, b.Recommendation);
        Assert.True(a.LateJobs.SequenceEqual(b.LateJobs));
    }
}
