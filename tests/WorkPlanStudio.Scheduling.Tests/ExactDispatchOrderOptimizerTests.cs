namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The exhaustive optimizer. Its value is that it is exact within a stated model,
/// so these tests pin both the exactness and the limits of the claim.
/// </summary>
public class ExactDispatchOrderOptimizerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void It_evaluates_every_order()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [Machine(1)],
            Job(1, Step(10, 1, 100)), Job(2, Step(10, 1, 200)), Job(3, Step(10, 1, 300)));

        Assert.Equal(6, ExactDispatchOrderOptimizer.Run(context, Ct).EvaluatedOrders);   // 3! = 6
    }

    [Fact]
    public void It_finds_the_order_the_heuristic_would_have_to_search_for()
    {
        // The urgent job is last in the natural order and only a reordering saves it.
        var context = Context(
            new SchedulingParameters { DueDateRule = DueDateRule.Explicit, MultiStartRuns = 1, LocalSearchMaxSteps = 0 },
            [Machine(1)],
            DueAt(1, 100_000, Step(10, 1, 1000)),
            DueAt(2, 100_000, Step(10, 1, 1000)),
            DueAt(3, 1_100, Step(10, 1, 1000)));

        var exact = ExactDispatchOrderOptimizer.Run(context, Ct);

        Assert.Equal(0, exact.Result.Evaluation.LateJobCount);
    }

    /// <summary>
    /// Sampling on an instance the sampler cannot exhaust. Nine jobs is 362 880
    /// orders against 400 draws, so "no sample beat the exact result" is a real
    /// statement — on the four-job fixture this used to run on, the 24 orders the
    /// optimizer had just enumerated were the only 24 the sampler could draw, and
    /// <c>min(S) ≤ s ∀ s ∈ S</c> is not an assertion.
    /// <para>
    /// The second assertion is the falsifiable one: being no worse than 400 random
    /// orders is weak, so the exact result also has to beat their median by a
    /// stated margin.
    /// </para>
    /// </summary>
    [Fact]
    public void No_sampled_order_beats_it_and_it_clears_the_median_by_a_margin()
    {
        var context = NineJobInstance();
        var due = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();

        double exact = ExactDispatchOrderOptimizer.Run(context, Ct).Result.Evaluation.Penalty;

        var sampled = new List<double>();
        for (int seed = 1; seed <= 400; seed++)
        {
            var order = Enumerable.Range(0, context.Jobs.Count).ToArray();
            new DeterministicRandom(seed).Shuffle(order);
            double penalty = ScheduleEvaluator.Evaluate(scheduler.Run(context, order, due, Ct), context).Penalty;

            Assert.True(penalty >= exact - 1e-9, $"seed {seed} found {penalty} < exact {exact}");
            sampled.Add(penalty);
        }

        sampled.Sort();
        double median = sampled[sampled.Count / 2];
        Assert.True(exact < median * 0.95, $"exact {exact:F2} is not 5 % better than the median sample {median:F2}");
    }

    /// <summary>
    /// Nine jobs — <see cref="ExactDispatchOrderOptimizer.MaxJobs"/>, the size the
    /// XML doc claims is "roughly a second". 362 880 dispatches.
    /// </summary>
    private static SchedulingContext NineJobInstance()
    {
        var rng = new DeterministicRandom(4242);
        var machines = Enumerable.Range(1, 3).Select(id => Machine(id)).ToArray();

        var jobs = new ProductionJob[ExactDispatchOrderOptimizer.MaxJobs];
        for (int j = 0; j < jobs.Length; j++)
        {
            var steps = new List<JobStep>();
            int stepCount = 1 + rng.NextInt(3);
            for (int s = 0; s < stepCount; s++)
                steps.Add(new JobStep(s + 1, 1 + rng.NextInt(machines.Length), 300 + rng.NextInt(3000)));

            jobs[j] = new ProductionJob
            {
                Id = j + 1,
                Reference = $"J{j + 1}",
                Weight = 1 + rng.NextInt(4),
                Steps = steps
            };
        }

        return new SchedulingContext(jobs, machines, new SchedulingParameters
        {
            DispatchRule = DispatchRule.EarliestDueDate,
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 1.2,
            MultiStartRuns = 1,
            LocalSearchMaxSteps = 0,
            Seed = 4242
        });
    }

    /// <summary>
    /// The largest instance it accepts really is enumerated, and the claim that
    /// this is about a second is worth having a number for rather than a comment.
    /// </summary>
    [Fact]
    public void It_enumerates_the_largest_instance_it_accepts()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = ExactDispatchOrderOptimizer.Run(NineJobInstance(), Ct);
        stopwatch.Stop();

        long factorial = Enumerable.Range(1, ExactDispatchOrderOptimizer.MaxJobs).Aggregate(1L, (a, b) => a * b);
        Assert.Equal(factorial, result.EvaluatedOrders);
        Assert.True(stopwatch.ElapsedMilliseconds < 20_000,
            $"9! = {factorial} orders took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void It_refuses_instances_it_cannot_enumerate()
    {
        var jobs = Enumerable.Range(1, ExactDispatchOrderOptimizer.MaxJobs + 1)
            .Select(i => Job(i, Step(10, 1, 100)))
            .ToArray();
        var context = Context(RuleOnly(DispatchRule.Fifo), [Machine(1)], jobs);

        Assert.False(ExactDispatchOrderOptimizer.CanEnumerate(context.Jobs.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExactDispatchOrderOptimizer.Run(context, Ct));
    }

    [Fact]
    public void An_empty_instance_is_handled()
    {
        var result = ExactDispatchOrderOptimizer.Run(
            new SchedulingContext([], [Machine(1)], new SchedulingParameters()), Ct);

        Assert.Empty(result.Result.Schedule.Operations);
        Assert.Equal(1, result.EvaluatedOrders);
    }

    [Fact]
    public void It_is_deterministic()
    {
        var a = ExactDispatchOrderOptimizer.Run(SearchTests.MediumScenario(DispatchRule.LongestProcessingTime), Ct);
        var b = ExactDispatchOrderOptimizer.Run(SearchTests.MediumScenario(DispatchRule.LongestProcessingTime), Ct);

        Assert.Equal(a.Result.Schedule.Signature(), b.Result.Schedule.Signature());
    }

    [Fact]
    public void It_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ExactDispatchOrderOptimizer.Run(SearchTests.MediumScenario(DispatchRule.Fifo), cancelled.Token));
    }
}
