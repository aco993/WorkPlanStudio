namespace WorkPlanStudio.Scheduling.Tests;

public class DispatchRuleTests
{
    // Three jobs competing for one work center. Index → (id, total, release, weight):
    //   0 → (1, 300, 0,  1)
    //   1 → (2, 100, 50, 1)
    //   2 → (3, 200, 0,  4)
    // Explicit targets: J1=600, J2=250, J3=400.
    private static (SchedulingContext Context, Dictionary<int, long> Due) Fixture(DispatchRule rule)
    {
        var machines = new[] { Machine(1) };
        var jobs = new[]
        {
            Weighted(1, 1.0, Step(10, 1, 300)),
            Released(2, 50, Step(10, 1, 100)),
            Weighted(3, 4.0, Step(10, 1, 200)),
        };
        var ctx = Context(RuleOnly(rule), machines, jobs);
        var due = new Dictionary<int, long> { [1] = 600, [2] = 250, [3] = 400 };
        return (ctx, due);
    }

    [Theory]
    [InlineData(DispatchRule.Fifo, new[] { 0, 2, 1 })]                          // by release, then id
    [InlineData(DispatchRule.ShortestProcessingTime, new[] { 1, 2, 0 })]        // 100, 200, 300
    [InlineData(DispatchRule.LongestProcessingTime, new[] { 0, 2, 1 })]         // 300, 200, 100
    [InlineData(DispatchRule.EarliestDueDate, new[] { 1, 2, 0 })]               // 250, 400, 600
    [InlineData(DispatchRule.CriticalRatio, new[] { 0, 2, 1 })]                 // 2.0, 2.0, 2.5 (tie by id)
    [InlineData(DispatchRule.WeightedShortestProcessingTime, new[] { 2, 1, 0 })] // 50, 100, 300
    public void Rule_produces_the_expected_priority_order(DispatchRule rule, int[] expected)
    {
        var (ctx, due) = Fixture(rule);
        Assert.Equal(expected, PriorityOrdering.For(ctx, due));
    }

    [Fact]
    public void Earliest_due_date_sequences_the_urgent_job_first_on_a_shared_machine()
    {
        var machines = new[] { Machine(1) };
        var loose = DueAt(1, 100_000, Step(10, 1, 100));
        var urgent = DueAt(2, 200, Step(10, 1, 100));
        var ctx = Context(RuleOnly(DispatchRule.EarliestDueDate, DueDateRule.Explicit), machines, loose, urgent);

        var result = new SchedulingEngine().Run(ctx);

        Assert.Equal(0, result.Schedule.Operations.Single(o => o.JobId == 2).StartSeconds);   // urgent first
        Assert.Equal(100, result.Schedule.Operations.Single(o => o.JobId == 1).StartSeconds);  // loose waits
    }

    [Fact]
    public void Different_rules_produce_different_schedules_on_a_non_trivial_problem()
    {
        // The dispatch rule sets the sequence on contended machines, so on a
        // problem with real contention two rules land on different schedules.
        // (On a tiny problem the optimiser would converge to the same optimum —
        // which is why the sample data ships seven competing jobs.)
        var edd = ScheduleUnder(DispatchRule.EarliestDueDate);
        var lpt = ScheduleUnder(DispatchRule.LongestProcessingTime);

        Assert.NotEqual(edd.Signature(), lpt.Signature());

        static Schedule ScheduleUnder(DispatchRule rule)
        {
            var ctx = SearchTests.MediumScenario(rule, multiStart: 1, localSearch: 0);
            var due = DueDateAssigner.Assign(ctx);
            return new DispatchScheduler().Run(ctx, PriorityOrdering.For(ctx, due), due);
        }
    }

    [Fact]
    public void Ties_are_broken_by_job_id_deterministically()
    {
        var machines = new[] { Machine(1) };
        var due = new Dictionary<int, long> { [7] = 500, [3] = 500 };
        var ctx = Context(RuleOnly(DispatchRule.ShortestProcessingTime), machines,
            Job(7, Step(10, 1, 100)), Job(3, Step(10, 1, 100)));

        // jobs: index 0 = id 7, index 1 = id 3 → lower id (3) wins the tie → [1, 0].
        Assert.Equal(new[] { 1, 0 }, PriorityOrdering.For(ctx, due));
    }
}
