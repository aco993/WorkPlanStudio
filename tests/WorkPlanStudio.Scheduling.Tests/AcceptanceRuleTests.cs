using WorkPlanStudio.Scheduling.Testing;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// What the acceptance rule is for. A hill climb over the insertion neighbourhood
/// can only be as good as the part of the neighbourhood the budget reaches, and
/// at realistic sizes the budget reaches a small part of it — so the rule that
/// decides when to adopt a move decides how much of the sequence the search can
/// touch at all. See ADR 0022 for the measurements behind the default.
/// </summary>
public class AcceptanceRuleTests
{
    private static SchedulingContext Instance(int jobs, LocalSearchAcceptance acceptance, int budget, int starts = 1) =>
        ProblemFactory.Build(
            jobs, operationsPerJob: 6, workCenters: 10, capacity: 2,
            multiStart: starts, localSearch: budget, acceptance: acceptance);

    /// <summary>
    /// The failure the default exists to avoid. One steepest-descent pass at 100
    /// jobs is 9 900 neighbours, so a 2 000-step budget never finishes it: the
    /// sweep stops around source position 20 and adopts a single move, leaving
    /// four fifths of the sequence untouched. The other two rules adopt as they go
    /// and reach the back of the sequence.
    /// </summary>
    [Fact]
    public void Steepest_descent_adopts_one_move_where_the_others_adopt_many()
    {
        var context = Instance(100, LocalSearchAcceptance.SteepestDescent, budget: 2_000);
        var dueByJob = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();
        var start = PriorityOrdering.For(context, dueByJob);

        LocalSearchResult Descend(LocalSearchAcceptance acceptance)
        {
            var ctx = context.WithParameters(context.Parameters with { LocalSearchAcceptance = acceptance });
            var schedule = scheduler.Run(ctx, start, dueByJob, Ct);
            return LocalSearch.Improve(
                scheduler, ctx, dueByJob, start, schedule, ScheduleEvaluator.Evaluate(schedule, ctx), 2_000, Ct);
        }

        var steepest = Descend(LocalSearchAcceptance.SteepestDescent);
        var best = Descend(LocalSearchAcceptance.BestInsertion);
        var first = Descend(LocalSearchAcceptance.FirstImprovement);

        Assert.Equal(2_000, steepest.StepsUsed);
        Assert.Equal(1, steepest.AdoptedMoves);
        Assert.True(best.AdoptedMoves >= 4, $"best-insertion adopted {best.AdoptedMoves} moves out of {best.StepsUsed} steps");
        Assert.True(first.AdoptedMoves >= 4, $"first-improvement adopted {first.AdoptedMoves} moves out of {first.StepsUsed} steps");
    }

    /// <summary>
    /// Every rule is a descent: whatever it adopts, the result can never be worse
    /// than the order it started from. This is the property the whole design rests
    /// on — restart 0 is the rule order, so the engine cannot be worse than the
    /// pure dispatch rule.
    /// </summary>
    [Theory]
    [InlineData(LocalSearchAcceptance.FirstImprovement)]
    [InlineData(LocalSearchAcceptance.BestInsertion)]
    [InlineData(LocalSearchAcceptance.SteepestDescent)]
    public void No_acceptance_rule_can_return_a_worse_order(LocalSearchAcceptance acceptance)
    {
        foreach (int jobs in new[] { 2, 5, 20, 60 })
        {
            var context = Instance(jobs, acceptance, budget: 500);
            var dueByJob = DueDateAssigner.Assign(context);
            var scheduler = new DispatchScheduler();
            var start = PriorityOrdering.For(context, dueByJob);
            var schedule = scheduler.Run(context, start, dueByJob, Ct);
            var evaluation = ScheduleEvaluator.Evaluate(schedule, context);

            var result = LocalSearch.Improve(scheduler, context, dueByJob, start, schedule, evaluation, 500, Ct);

            Assert.True(result.Evaluation.Penalty <= evaluation.Penalty + 1e-9,
                $"{acceptance} at {jobs} jobs regressed from {evaluation.Penalty} to {result.Evaluation.Penalty}");
        }
    }

    [Theory]
    [InlineData(LocalSearchAcceptance.FirstImprovement)]
    [InlineData(LocalSearchAcceptance.BestInsertion)]
    [InlineData(LocalSearchAcceptance.SteepestDescent)]
    public void Every_acceptance_rule_stays_inside_its_budget(LocalSearchAcceptance acceptance)
    {
        foreach (int budget in new[] { 1, 7, 99, 1_000 })
        {
            var context = Instance(30, acceptance, budget);
            var dueByJob = DueDateAssigner.Assign(context);
            var scheduler = new DispatchScheduler();
            var start = PriorityOrdering.For(context, dueByJob);
            var schedule = scheduler.Run(context, start, dueByJob, Ct);

            var result = LocalSearch.Improve(
                scheduler, context, dueByJob, start, schedule, ScheduleEvaluator.Evaluate(schedule, context), budget, Ct);

            Assert.True(result.StepsUsed <= budget, $"{acceptance} used {result.StepsUsed} of {budget}");
        }
    }

    /// <summary>
    /// A rule that reaches the same local optimum from the same start must return
    /// the same order every time, whatever else changed around it.
    /// </summary>
    [Theory]
    [InlineData(LocalSearchAcceptance.FirstImprovement)]
    [InlineData(LocalSearchAcceptance.BestInsertion)]
    [InlineData(LocalSearchAcceptance.SteepestDescent)]
    public void Every_acceptance_rule_is_deterministic(LocalSearchAcceptance acceptance)
    {
        var context = Instance(40, acceptance, budget: 1_500, starts: 4);

        Assert.Equal(
            new SchedulingEngine().Run(context).Schedule.Signature(),
            new SchedulingEngine().Run(context).Schedule.Signature());
    }

    /// <summary>
    /// The budget has to buy something. Tripling it on an instance large enough
    /// that no rule converges must reduce the penalty, or the parameter is
    /// decoration.
    /// </summary>
    [Fact]
    public void A_larger_budget_finds_a_strictly_better_schedule()
    {
        double small = new SchedulingEngine().Run(Instance(100, LocalSearchAcceptance.BestInsertion, 2_000)).Evaluation.Penalty;
        double large = new SchedulingEngine().Run(Instance(100, LocalSearchAcceptance.BestInsertion, 12_000)).Evaluation.Penalty;

        Assert.True(large < small - 1e-9, $"12 000 steps scored {large}, 2 000 steps scored {small}");
    }
}
