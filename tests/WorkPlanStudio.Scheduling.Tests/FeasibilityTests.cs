namespace WorkPlanStudio.Scheduling.Tests;

public class FeasibilityTests
{
    public static IEnumerable<object[]> RulesAndSeeds()
    {
        foreach (var rule in Enum.GetValues<DispatchRule>())
            foreach (var seed in new[] { 1, 7, 42, 20260616 })
                yield return [rule, seed];
    }

    [Theory]
    [MemberData(nameof(RulesAndSeeds))]
    public void Every_generated_schedule_is_feasible(DispatchRule rule, int seed)
    {
        var ctx = SearchTests.MediumScenario(rule, seed: seed);

        var result = new SchedulingEngine().Run(ctx);

        Feasibility.AssertFeasible(result.Schedule, ctx);
        Assert.Equal(ctx.Jobs.Sum(j => j.Steps.Count), result.Schedule.Operations.Count);
        Assert.Equal(ctx.Jobs.Count, result.Schedule.Jobs.Count);
    }
}
