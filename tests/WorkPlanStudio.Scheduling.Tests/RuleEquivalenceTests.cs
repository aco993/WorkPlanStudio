namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The dispatch rule and the target-date rule are not independent: several
/// combinations are provably the same ordering. The UI offers 6 × 4 dispatch /
/// target combinations, so it matters that these collapses are known, deliberate
/// and pinned rather than discovered by a user wondering why nothing changed.
/// <para>
/// With all jobs released at second 0 and <c>P</c> = total processing time:
/// </para>
/// <list type="bullet">
/// <item>TWK sets <c>due = f · P</c>, strictly increasing in <c>P</c>, so
/// <b>EDD ≡ SPT</b>; and <c>CR = due / P = f</c> is constant, so <b>CR ≡ FIFO</b>.</item>
/// <item>SLK sets <c>due = P + s</c>, so <b>EDD ≡ SPT</b>; and <c>CR = (P + s) / P</c>
/// decreases in <c>P</c>, so <b>CR ≡ LPT</b>.</item>
/// <item>CON sets <c>due = c</c> for every job, so <b>EDD ≡ FIFO</b>; and
/// <c>CR = c / P</c> decreases in <c>P</c>, so <b>CR ≡ LPT</b>.</item>
/// <item>NOP keys the target on the operation count instead of the work content,
/// which is the only shipped rule that decouples all six.</item>
/// </list>
/// </summary>
public class RuleEquivalenceTests
{
    /// <summary>Jobs with distinct processing times, step counts and weights, all released at 0.</summary>
    private static SchedulingContext Instance(DispatchRule rule, DueDateRule dueRule) =>
        Context(
            new SchedulingParameters
            {
                DispatchRule = rule,
                DueDateRule = dueRule,
                TwkFlowFactor = 2.0,
                SlackSeconds = 7200,
                ConstantAllowanceSeconds = 28800,
                NopSecondsPerOp = 3600
            },
            [Machine(1), Machine(2)],
            Weighted(1, 1.0, Step(10, 1, 3000), Step(20, 2, 1000)),
            Weighted(2, 3.0, Step(10, 2, 900)),
            Weighted(3, 8.0, Step(10, 1, 5000), Step(20, 2, 200), Step(30, 1, 400)),
            Weighted(4, 2.0, Step(10, 2, 2000), Step(20, 1, 100)));

    private static int[] OrderUnder(DispatchRule rule, DueDateRule dueRule)
    {
        var context = Instance(rule, dueRule);
        return PriorityOrdering.For(context, DueDateAssigner.Assign(context));
    }

    [Theory]
    // TWK: due is a strictly increasing function of processing time.
    [InlineData(DueDateRule.TotalWorkContent, DispatchRule.EarliestDueDate, DispatchRule.ShortestProcessingTime)]
    [InlineData(DueDateRule.TotalWorkContent, DispatchRule.CriticalRatio, DispatchRule.Fifo)]
    // SLK: due = P + constant.
    [InlineData(DueDateRule.EqualSlack, DispatchRule.EarliestDueDate, DispatchRule.ShortestProcessingTime)]
    [InlineData(DueDateRule.EqualSlack, DispatchRule.CriticalRatio, DispatchRule.LongestProcessingTime)]
    // CON: every job gets the same target.
    [InlineData(DueDateRule.ConstantAllowance, DispatchRule.EarliestDueDate, DispatchRule.Fifo)]
    [InlineData(DueDateRule.ConstantAllowance, DispatchRule.CriticalRatio, DispatchRule.LongestProcessingTime)]
    public void Rules_that_collapse_under_a_target_rule_produce_the_same_order(
        DueDateRule dueRule, DispatchRule rule, DispatchRule equivalentTo)
    {
        Assert.Equal(OrderUnder(equivalentTo, dueRule), OrderUnder(rule, dueRule));
    }

    [Fact]
    public void Number_of_operations_targets_decouple_all_six_rules()
    {
        var orders = Enum.GetValues<DispatchRule>()
            .Select(rule => string.Join(",", OrderUnder(rule, DueDateRule.NumberOfOperations)))
            .ToArray();

        Assert.Equal(orders.Length, orders.Distinct().Count());
    }

    [Fact]
    public void Weighted_rule_differs_from_its_unweighted_counterpart()
    {
        Assert.NotEqual(
            OrderUnder(DispatchRule.ShortestProcessingTime, DueDateRule.TotalWorkContent),
            OrderUnder(DispatchRule.WeightedShortestProcessingTime, DueDateRule.TotalWorkContent));
    }

    /// <summary>The headline number: six rules, four schedules, on the default target rule.</summary>
    [Fact]
    public void Total_work_content_collapses_the_six_rules_to_four_orders()
    {
        var distinct = Enum.GetValues<DispatchRule>()
            .Select(rule => string.Join(",", OrderUnder(rule, DueDateRule.TotalWorkContent)))
            .Distinct()
            .Count();

        Assert.Equal(4, distinct);
    }

    // ----- what the engine reports to the UI -----

    [Fact]
    public void The_engine_reports_the_rules_a_user_could_pick_instead()
    {
        var context = Instance(DispatchRule.CriticalRatio, DueDateRule.TotalWorkContent);

        var equivalent = new SchedulingEngine().Run(context).EquivalentRules;

        Assert.Contains(DispatchRule.Fifo, equivalent);
        Assert.DoesNotContain(DispatchRule.CriticalRatio, equivalent);   // never itself
    }

    [Fact]
    public void A_rule_with_no_twin_reports_nothing()
    {
        var context = Instance(DispatchRule.LongestProcessingTime, DueDateRule.TotalWorkContent);

        Assert.Empty(new SchedulingEngine().Run(context).EquivalentRules);
    }
}
