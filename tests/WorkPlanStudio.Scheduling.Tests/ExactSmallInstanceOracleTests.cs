namespace WorkPlanStudio.Scheduling.Tests;

public sealed class ExactSmallInstanceOracleTests
{
    [Fact]
    public void Heuristic_matches_the_exhaustive_optimum_for_a_small_reference_instance()
    {
        var parameters = new SchedulingParameters
        {
            DueDateRule = DueDateRule.Explicit,
            DispatchRule = DispatchRule.EarliestDueDate,
            MultiStartRuns = 8,
            LocalSearchMaxSteps = 100,
            Seed = 20260714
        };
        var context = Scenario.Context(parameters, [Scenario.Machine(1)],
            Scenario.DueAt(1, 1, Scenario.Step(1, 1, 1)),
            Scenario.DueAt(2, 3, Scenario.Step(1, 1, 2)),
            Scenario.DueAt(3, 6, Scenario.Step(1, 1, 3)));
        var due = DueDateAssigner.Assign(context);
        var dispatcher = new DispatchScheduler();
        var exactBest = Permutations([0, 1, 2])
            .Select(order => ScheduleEvaluator.Evaluate(dispatcher.Run(context, order, due), context).Penalty)
            .Min();

        var heuristic = new SchedulingEngine().Run(context);

        Assert.Equal(exactBest, heuristic.Evaluation.Penalty, precision: 10);
    }

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length == 1)
        {
            yield return values;
            yield break;
        }
        for (var index = 0; index < values.Length; index++)
        {
            var head = values[index];
            var rest = values.Where((_, candidate) => candidate != index).ToArray();
            foreach (var permutation in Permutations(rest))
                yield return [head, .. permutation];
        }
    }
}
