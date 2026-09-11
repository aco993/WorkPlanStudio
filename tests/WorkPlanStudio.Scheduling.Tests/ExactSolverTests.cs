using System.Diagnostics;
using WorkPlanStudio.Scheduling.Exact;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The exact solver's own guarantees: that what it calls optimal is optimal, that
/// what it cannot prove it does not claim, that its schedules are feasible under
/// an independent checker, and that it reproduces itself.
/// <para>
/// The first test is the one the audit wrote this stream for. The shipped
/// "exact optimiser" searches job permutations and hands each to the greedy
/// dispatcher, and on a two-job, three-work-center instance that is 50 % off the
/// real answer. Pinning both numbers in one test makes the difference between the
/// two ideas of "exact" a fact in CI rather than a paragraph in a document.
/// </para>
/// </summary>
public class ExactSolverTests
{
    /// <summary>Makespan only, nothing late, no search: the objective is the schedule's length.</summary>
    private static SchedulingParameters MakespanOnly() => new()
    {
        DueDateRule = DueDateRule.ConstantAllowance,
        ConstantAllowanceSeconds = 1_000_000,
        MakespanWeight = 1.0,
        TardinessWeight = 0.0,
        LatePenalty = 0.0,
        MultiStartRuns = 1,
        LocalSearchMaxSteps = 0
    };

    /// <summary>
    /// J1 runs M1 → M2 → M3 and J2 runs M3 → M2 → M1, ten seconds each. Whichever
    /// job the dispatcher takes first, it finishes that job's whole routing before
    /// starting the other, so the two jobs never interleave and the makespan is 60.
    /// Interleaving them gives 40, and no permutation of jobs can express it.
    /// </summary>
    [Fact]
    public void The_counterexample_that_costs_the_permutation_optimiser_fifty_percent()
    {
        var machines = new[] { Machine(1), Machine(2), Machine(3) };
        var context = Context(
            MakespanOnly(),
            machines,
            Job(1, Step(10, 1, 10), Step(20, 2, 10), Step(30, 3, 10)),
            Job(2, Step(10, 3, 10), Step(20, 2, 10), Step(30, 1, 10)));

        long overEveryJobOrder = ExhaustiveDispatchOrderSearch.Run(context, Ct).Result.Schedule.MakespanSeconds;
        var exact = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

        Assert.Equal(60, overEveryJobOrder);
        Assert.Equal(ExactSolutionStatus.Optimal, exact.Status);
        Assert.Equal(40, exact.MakespanSeconds);
        Assert.Equal(40 / 3600.0, exact.OptimalPenalty, 9);

        // The 50 % the audit measured, as a number rather than as prose.
        Assert.Equal(0.5, (overEveryJobOrder - exact.MakespanSeconds) / (double)exact.MakespanSeconds, 9);
        Feasibility.AssertFeasible(exact.Schedule!, context);
    }

    /// <summary>
    /// Where the dispatch-order model is not a restriction — one work center, one
    /// operation per job — the permutation optimiser and the branch-and-bound
    /// search the same set and must agree exactly. Two independent
    /// implementations landing on the same number on 24 instances is the check
    /// that keeps the branch-and-bound honest at the bottom end.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(101)]
    [InlineData(20260911)]
    [InlineData(31337)]
    public void The_permutation_optimiser_and_the_branch_and_bound_agree_where_the_models_coincide(int seed)
    {
        foreach (int jobCount in new[] { 3, 5, 6, 7 })
        {
            var context = SingleWorkCentreInstance(seed, jobCount);

            double byEnumeration = ExhaustiveDispatchOrderSearch.Run(context, Ct).Result.Evaluation.Penalty;
            var exact = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

            Assert.Equal(ExactSolutionStatus.Optimal, exact.Status);
            Assert.Equal(byEnumeration, exact.OptimalPenalty, 9);
        }
    }

    /// <summary>
    /// A single job has exactly one schedule, so the dispatcher and the solver must
    /// place it identically. Across 240 generated calendars — shifts, breaks,
    /// phases, bridgeable gaps and blackouts — that is a working equivalence test
    /// between two independent implementations of the placement rule, through the
    /// public surface of both.
    /// </summary>
    [Fact]
    public void The_two_placement_implementations_agree_on_generated_calendars()
    {
        var random = new DeterministicRandom(20260911);
        int compared = 0;

        for (int trial = 0; trial < 240; trial++)
        {
            var machine = RandomCalendar(random, id: 1);
            long release = random.NextInt(4) * 3600L * random.NextInt(30);
            var steps = new List<JobStep>();
            int stepCount = 1 + random.NextInt(3);
            for (int step = 0; step < stepCount; step++)
                steps.Add(new JobStep((step + 1) * 10, 1, random.NextInt(3) * 1800L));

            var job = new ProductionJob
            {
                Id = 1,
                Reference = "J1",
                ReleaseSeconds = release,
                Steps = steps
            };

            var context = new SchedulingContext([job], [machine], MakespanOnly());
            var dispatched = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context), Ct);
            var exact = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

            Assert.Equal(ExactSolutionStatus.Optimal, exact.Status);
            Assert.Equal(dispatched.Signature(), exact.Schedule!.Signature());
            compared++;
        }

        Assert.Equal(240, compared);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(97)]
    public void Every_schedule_the_solver_returns_is_feasible(int seed)
    {
        var context = GeneratedInstance(seed);
        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);
        Feasibility.AssertFeasible(solution.Schedule!, context);
    }

    /// <summary>
    /// The heuristic can never beat the optimum. It is a trivial-sounding property
    /// and it is the one that catches a solver whose model has drifted from the
    /// engine's: an "optimum" the engine can undercut is not an optimum, it is a
    /// different problem.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(97)]
    [InlineData(20260616)]
    public void No_schedule_the_engine_finds_is_better_than_the_optimum(int seed)
    {
        var context = GeneratedInstance(seed);
        var optimum = ExactJobShopSolver.Solve(context, cancellationToken: Ct).OptimalPenalty;
        double found = new SchedulingEngine().RunCancellable(context, Ct).Evaluation.Penalty;

        Assert.True(found >= optimum - 1e-6, $"engine {found:F6} beat the proved optimum {optimum:F6}");
    }

    [Fact]
    public void The_same_instance_under_the_same_limits_gives_the_same_answer()
    {
        var context = GeneratedInstance(11);
        var options = ExactSolverOptions.Default with { NodeLimit = 4_000 };

        var first = ExactJobShopSolver.Solve(context, options, Ct);
        var second = ExactJobShopSolver.Solve(context, options, Ct);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.NodesExplored, second.NodesExplored);
        Assert.Equal(first.NodesPruned, second.NodesPruned);
        Assert.Equal(first.BestBound, second.BestBound, 12);
        Assert.Equal(first.Schedule!.Signature(), second.Schedule!.Signature());
    }

    /// <summary>
    /// A budget that runs out has to change the answer's label, not only its
    /// quality. This is the property that separates this solver from the one the
    /// documentation used to quote.
    /// </summary>
    [Fact]
    public void A_solver_that_runs_out_of_nodes_reports_a_gap_and_refuses_to_call_it_optimal()
    {
        var context = GeneratedInstance(seed: 5, jobs: 7, stepsPerJob: 3, workCentres: 3);
        var truncated = ExactJobShopSolver.Solve(context, ExactSolverOptions.Default with { NodeLimit = 60 }, Ct);

        Assert.Equal(ExactSolutionStatus.FeasibleWithGap, truncated.Status);
        Assert.False(truncated.IsOptimal);
        Assert.NotNull(truncated.Schedule);
        Assert.Throws<InvalidOperationException>(() => truncated.OptimalPenalty);

        double optimum = ExactJobShopSolver.Solve(context, cancellationToken: Ct).OptimalPenalty;
        Assert.True(truncated.BestBound <= optimum + 1e-9, $"bound {truncated.BestBound:F6} exceeds the optimum {optimum:F6}");
        Assert.True(truncated.Penalty >= optimum - 1e-9);
        Assert.True(truncated.AbsoluteGap >= 0);
    }

    [Fact]
    public void An_instance_larger_than_the_limit_is_refused_rather_than_approximated()
    {
        var context = GeneratedInstance(seed: 2, jobs: 6, stepsPerJob: 3, workCentres: 3);
        var options = ExactSolverOptions.Default with { MaxOperations = 10 };

        Assert.False(ExactJobShopSolver.CanSolve(context, options));
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ExactJobShopSolver.Solve(context, options, Ct));
        Assert.Contains("at most 10 operations", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_solve_stops_instead_of_answering()
    {
        var context = GeneratedInstance(seed: 5, jobs: 7, stepsPerJob: 4, workCentres: 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ExactJobShopSolver.Solve(context, null, cancellation.Token));
    }

    /// <summary>
    /// Grouping two change-over families beats alternating them, and the optimum
    /// has to know that. Without setup in the model the answer here would be 40;
    /// the solver returns 50 because one change-over is unavoidable, and 70 —
    /// which alternating costs — is what a solver that ignored the matrix in its
    /// <i>search</i> rather than only in its bound would return.
    /// </summary>
    [Fact]
    public void Change_over_families_are_part_of_the_optimum()
    {
        var machine = new MachineCapacity(1, "WC-1")
        {
            SetupDurations = [new SetupDuration("A", "B", 10), new SetupDuration("B", "A", 10)]
        };

        var jobs = new[]
        {
            Job(1, new JobStep(10, 1, 10, "A")),
            Job(2, new JobStep(10, 1, 10, "B")),
            Job(3, new JobStep(10, 1, 10, "A")),
            Job(4, new JobStep(10, 1, 10, "B"))
        };

        var context = Context(MakespanOnly(), [machine], jobs);
        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);
        Assert.Equal(50, solution.MakespanSeconds);
        Feasibility.AssertFeasible(solution.Schedule!, context);
    }

    /// <summary>
    /// Two slots halve a bottleneck, and the symmetry break that offers only one
    /// idle slot per work center must not cost the solver the second one.
    /// </summary>
    [Fact]
    public void Parallel_slots_are_both_used_when_that_is_optimal()
    {
        var context = Context(
            MakespanOnly(),
            [Machine(1, capacity: 2)],
            Job(1, Step(10, 1, 100)),
            Job(2, Step(10, 1, 100)),
            Job(3, Step(10, 1, 100)),
            Job(4, Step(10, 1, 100)));

        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);
        Assert.Equal(200, solution.MakespanSeconds);
        Assert.Equal(2, solution.Schedule!.Operations.Select(o => o.SlotIndex).Distinct().Count());
        Feasibility.AssertFeasible(solution.Schedule, context);
    }

    /// <summary>
    /// A zero-length step is how a routing models an inspection gate. It occupies no
    /// machine time, which the bound has to cope with: the first version of Jackson's
    /// preemptive schedule here counted such an operation as remaining work, found
    /// nothing it could run, and never terminated. One work center whose whole
    /// remaining set is zero-length is the shape that reproduces it.
    /// </summary>
    [Fact]
    public void A_zero_length_inspection_gate_does_not_stall_the_machine_bound()
    {
        var context = Context(
            MakespanOnly(),
            [Machine(1), Machine(2)],
            Job(1, Step(10, 1, 0), Step(20, 2, 600)),
            Job(2, Step(10, 1, 0), Step(20, 2, 900)));

        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);
        Assert.Equal(1_500, solution.MakespanSeconds);
        Feasibility.AssertFeasible(solution.Schedule!, context);
    }

    [Fact]
    public void A_release_date_is_a_hard_floor_on_the_optimum()
    {
        var context = Context(
            MakespanOnly(),
            [Machine(1)],
            Released(1, 5_000, Step(10, 1, 100)),
            Job(2, Step(10, 1, 100)));

        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);
        Assert.Equal(5_100, solution.MakespanSeconds);
    }

    /// <summary>
    /// An empty instance is optimal at zero, not a special case that throws.
    /// </summary>
    [Fact]
    public void An_empty_instance_is_optimal_at_zero()
    {
        var context = new SchedulingContext([], [Machine(1)], MakespanOnly());
        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);

        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);
        Assert.Equal(0, solution.OptimalPenalty);
        Assert.Empty(solution.Schedule!.Operations);
    }

    /// <summary>
    /// A tripwire, not a benchmark: the reference eighteen-operation instance has
    /// to stay inside a second on a laptop, because "small instances solve in the
    /// browser" is a claim this repository makes out loud.
    /// </summary>
    [Fact]
    public void The_reference_instance_is_proved_optimal_inside_a_second()
    {
        var context = GeneratedInstance(seed: 29, jobs: 6, stepsPerJob: 3, workCentres: 3);
        _ = ExactJobShopSolver.Solve(context, cancellationToken: Ct);   // warm the JIT

        var stopwatch = Stopwatch.StartNew();
        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);
        stopwatch.Stop();

        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);
        Assert.True(stopwatch.ElapsedMilliseconds < 1_000,
            $"18 operations took {stopwatch.ElapsedMilliseconds} ms over {solution.NodesExplored} nodes");
    }

    /// <summary>
    /// The search loop allocates nothing. Every array it needs — the state, the
    /// bound workspaces, the extension buffer — is sized once from the instance and
    /// overwritten in place, exactly as the engine's own candidate scoring is. The
    /// one thing that does grow is the state memo, so it is measured with the memo
    /// switched off, where the figure is the loop and nothing else.
    /// </summary>
    /// <remarks>
    /// Measured: 69 354 nodes for 10 672 bytes, which is the fixed set-up and not
    /// the nodes. With the memo on the same instance costs 244 464 bytes over
    /// 2 830 nodes — 24 times fewer nodes for the memory the memo holds, which is
    /// the trade the default makes.
    /// </remarks>
    [Fact]
    public void The_search_loop_itself_allocates_nothing_per_node()
    {
        var context = GeneratedInstance(seed: 29, jobs: 6, stepsPerJob: 3, workCentres: 3);
        var options = ExactSolverOptions.Default with { UseStateMemo = false };
        ExactSolution? solution = null;

        long allocated = Measure(() => solution = ExactJobShopSolver.Solve(context, options, Ct));

        Assert.Equal(ExactSolutionStatus.Optimal, solution!.Status);
        Assert.True(solution.NodesExplored > 20_000, $"expected a real search, got {solution.NodesExplored} nodes");
        Assert.True(allocated < 64 * 1024, $"a whole solve allocated {allocated} B");

        double perNode = (double)allocated / solution.NodesExplored;
        Assert.True(perNode < 1.0, $"{perNode:F2} B per node over {solution.NodesExplored} nodes");
    }

    /// <summary>
    /// With the memo on, the whole solve stays inside what the memo was allowed to
    /// hold — it stops growing at its capacity rather than following the tree.
    /// </summary>
    [Fact]
    public void The_state_memo_stays_inside_the_capacity_it_was_given()
    {
        var context = GeneratedInstance(seed: 3, jobs: 7, stepsPerJob: 3, workCentres: 3);
        var options = ExactSolverOptions.Default with { StateMemoCapacity = 4_096 };
        ExactSolution? solution = null;

        long allocated = Measure(() => solution = ExactJobShopSolver.Solve(context, options, Ct));

        // 4 096 states × (2 × 7 jobs + 2 × 3 slots) longs is 655 360 bytes; the
        // doubling that reaches it costs the same again, and the rest is set-up.
        Assert.Equal(ExactSolutionStatus.Optimal, solution!.Status);
        Assert.True(allocated < 2 * 1024 * 1024, $"a capped memo cost {allocated} B");
    }

    private static long Measure(Action work)
    {
        work();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ----- fixtures -----

    private static SchedulingContext SingleWorkCentreInstance(int seed, int jobCount)
    {
        var random = new DeterministicRandom(seed * 31 + jobCount);
        var jobs = new ProductionJob[jobCount];
        for (int j = 0; j < jobCount; j++)
        {
            jobs[j] = new ProductionJob
            {
                Id = j + 1,
                Reference = $"J{j + 1}",
                ReleaseSeconds = random.NextInt(3) * 600L,
                Weight = 1 + random.NextInt(4),
                Steps = [new JobStep(10, 1, 600 + random.NextInt(5) * 600L)]
            };
        }

        return new SchedulingContext(jobs, [Machine(1)], new SchedulingParameters
        {
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 1.5,
            Seed = seed
        });
    }

    private static SchedulingContext GeneratedInstance(
        int seed, int jobs = 5, int stepsPerJob = 3, int workCentres = 3)
    {
        var random = new DeterministicRandom(seed);
        var machines = Enumerable.Range(1, workCentres).Select(id => Machine(id)).ToArray();

        var list = new ProductionJob[jobs];
        for (int j = 0; j < jobs; j++)
        {
            var steps = new List<JobStep>();
            for (int s = 0; s < stepsPerJob; s++)
                steps.Add(new JobStep((s + 1) * 10, 1 + random.NextInt(workCentres), 600 + random.NextInt(10) * 300L));

            list[j] = new ProductionJob
            {
                Id = j + 1,
                Reference = $"J{j + 1}",
                ReleaseSeconds = random.NextInt(3) * 600L,
                Weight = 1 + random.NextInt(4),
                Steps = steps
            };
        }

        return new SchedulingContext(list, machines, new SchedulingParameters
        {
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 1.4,
            Seed = seed
        });
    }

    /// <summary>A day shift, a two-shift week, a continuous machine, sometimes with a blackout.</summary>
    private static MachineCapacity RandomCalendar(DeterministicRandom random, int id)
    {
        const long Day = 24 * 3600;
        int shape = random.NextInt(4);
        if (shape == 0)
            return new MachineCapacity(id, $"WC-{id}");

        var windows = shape == 1
            ? new List<CapacityWindow> { new(6 * 3600, 14 * 3600) }
            : [new CapacityWindow(6 * 3600, 10 * 3600), new CapacityWindow(10 * 3600 + 1800, 14 * 3600)];

        return new MachineCapacity(id, $"WC-{id}")
        {
            AvailabilityWindows = windows,
            CalendarPeriodSeconds = Day,
            CalendarPhaseSeconds = random.NextInt(3) * 3600L,
            MaxBridgeableGapSeconds = shape == 2 ? 1800 : 0,
            Blackouts = shape == 3 ? [new CapacityBlackout(2 * Day, 3 * Day, "maintenance")] : []
        };
    }
}
