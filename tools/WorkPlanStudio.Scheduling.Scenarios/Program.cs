using System.Diagnostics;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Scheduling.Exact;
using WorkPlanStudio.Scheduling.Testing;

// A measurement harness, not a benchmark: BenchmarkDotNet lives in
// tests/WorkPlanStudio.Benchmarks and answers "how fast". This answers "which
// setting is better", which needs many instances rather than many iterations.
//
//   dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios [mode]
//
//   scenarios    (default) the three reference sizes: time, allocation, penalty
//   acceptance   first-improvement vs steepest descent at equal budget, n = 5..100
//   restarts     what each extra multi-start restart buys, n = 5..100
//   allocation   bytes per evaluated candidate
//   explain      cost of one ScheduleExplainer.Explain on the medium problem
//   budget       penalty against wall clock on the reference medium instance
//   optimality   gap to the exact dispatch-order optimum on 7-job instances
//   exact        the 20-instance study: true optimum vs heuristic, as a markdown table
//   exactwall    where the branch-and-bound stops proving optimality, by instance shape
//   exactbudget  how many instances of each size are proved inside 1 s and inside 60 s
//   milp         writes an LP-format model per study instance into ./milp
string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "scenarios";

Console.WriteLine($"Runtime: {Environment.Version}; OS: {Environment.OSVersion}; CPU: {Environment.ProcessorCount}");
Console.WriteLine();

switch (mode)
{
    case "acceptance": Acceptance(); break;
    case "restarts": Restarts(); break;
    case "allocation": Allocation(); break;
    case "explain": Explain(); break;
    case "budget": Budget(); break;
    case "optimality": Optimality(); break;
    case "exact": Exact(); break;
    case "exactwall": ExactWall(); break;
    case "exactbudget": ExactBudget(); break;
    case "milp": Milp(args.Length > 1 ? args[1] : "milp"); break;
    default: Scenarios(); break;
}

static void Scenarios()
{
    var scenarios = new[]
    {
        new ScenarioDefinition("small", Jobs: 25, OperationsPerJob: 4, WorkCenters: 5, Capacity: 1, MultiStart: 4, LocalSearch: 500),
        new ScenarioDefinition("medium", Jobs: 100, OperationsPerJob: 6, WorkCenters: 10, Capacity: 2, MultiStart: 8, LocalSearch: 2_000),
        new ScenarioDefinition("large", Jobs: 250, OperationsPerJob: 8, WorkCenters: 20, Capacity: 2, MultiStart: 16, LocalSearch: 5_000)
    };

    Console.WriteLine("| Scenario | Jobs | Operations | Centers | Capacity | Starts | Local steps | Duration ms | Allocated MB | Peak working MB | Penalty | Deterministic |");
    Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");

    foreach (var definition in scenarios)
    {
        var context = ProblemFactory.Build(
            definition.Jobs, definition.OperationsPerJob, definition.WorkCenters,
            definition.Capacity, definition.MultiStart, definition.LocalSearch);
        _ = new SchedulingEngine().Run(context); // warm JIT outside the measurement

        Settle();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var first = new SchedulingEngine().Run(context);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var second = new SchedulingEngine().Run(context);

        Console.WriteLine(
            $"| {definition.Name} | {definition.Jobs} | {definition.Jobs * definition.OperationsPerJob} | " +
            $"{definition.WorkCenters} | {definition.Capacity} | {definition.MultiStart} | {definition.LocalSearch} | " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F1} | {allocated / 1024d / 1024d:F2} | " +
            $"{Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d:F1} | {first.Evaluation.Penalty:F4} | " +
            $"{first.Schedule.Signature() == second.Schedule.Signature()} |");
    }
}

// Both acceptance rules over the same fixed instances at the same budget. The
// budget is neighbour evaluations, so the two columns cost the same number of
// dispatches by construction and the penalty column is the whole comparison.
// Five instances per size, because one instance is an anecdote.
static void Acceptance()
{
    var rules = new[]
    {
        LocalSearchAcceptance.SteepestDescent,
        LocalSearchAcceptance.FirstImprovement,
        LocalSearchAcceptance.BestInsertion
    };

    Console.WriteLine("| Jobs | Budget | Pass | Steepest | FirstImprovement | BestInsertion | Best rule | Best vs steepest |");
    Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |");

    foreach (var shape in Shapes())
    {
        foreach (int budget in Budgets(shape.Jobs))
        {
            var means = new double[rules.Length];
            foreach (int seed in Seeds())
            {
                var instance = shape with { Variant = seed };
                for (int r = 0; r < rules.Length; r++)
                    means[r] += Measure(instance, budget, rules[r]).Penalty;
            }

            int n = Seeds().Length;
            for (int r = 0; r < means.Length; r++)
                means[r] /= n;

            int winner = 0;
            for (int r = 1; r < means.Length; r++)
            {
                if (means[r] < means[winner])
                    winner = r;
            }

            double delta = means[0] <= 0 ? 0 : (means[0] - means[winner]) / means[0];
            Console.WriteLine(
                $"| {shape.Jobs} | {budget} | {shape.Jobs * (shape.Jobs - 1)} | {means[0]:F3} | {means[1]:F3} | {means[2]:F3} | " +
                $"{rules[winner]} | {delta:P2} |");
        }
    }
}

// What a restart buys. Single-restart is the reference; a restart only earns its
// keep if it finds a strictly better order somewhere, on some instance.
static void Restarts()
{
    Console.WriteLine("| Jobs | Acceptance | Budget/restart | Starts | Mean penalty | Strictly better than 1 start | Mean ms | Mean allocated KB |");
    Console.WriteLine("| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |");

    foreach (var acceptance in Enum.GetValues<LocalSearchAcceptance>())
    {
        foreach (var shape in Shapes())
        {
            int budget = Budgets(shape.Jobs)[^1];
            var single = new Dictionary<int, double>();

            foreach (int starts in new[] { 1, 2, 4, 8 })
            {
                double penaltySum = 0, msSum = 0, allocSum = 0;
                int better = 0;
                foreach (int seed in Seeds())
                {
                    var run = Measure(shape with { Variant = seed }, budget, acceptance, starts);
                    penaltySum += run.Penalty;
                    msSum += run.Milliseconds;
                    allocSum += run.AllocatedBytes;
                    if (starts == 1)
                        single[seed] = run.Penalty;
                    else if (run.Penalty < single[seed] - 1e-9)
                        better++;
                }

                int n = Seeds().Length;
                Console.WriteLine(
                    $"| {shape.Jobs} | {acceptance} | {budget} | {starts} | {penaltySum / n:F3} | " +
                    $"{(starts == 1 ? "-" : $"{better}/{n}")} | {msSum / n:F1} | {allocSum / n / 1024d:F0} |");
            }
        }
    }
}

// Bytes per evaluated candidate: the number docs/PERFORMANCE.md quotes. Counted
// against the candidates the run actually built, which is
// restarts x (1 initial dispatch + the descent steps it used).
static void Allocation()
{
    Console.WriteLine("| Jobs | Ops | Starts | Budget | Candidates | Allocated bytes | Bytes/candidate | ms | us/candidate |");
    Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

    foreach (var shape in Shapes())
    {
        int budget = Budgets(shape.Jobs)[^1];
        const int starts = 8;
        var context = Context(shape, budget, LocalSearchAcceptance.BestInsertion, starts);
        _ = new SchedulingEngine().Run(context);

        Settle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var result = new SchedulingEngine().Run(context);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        long candidates = result.LocalSearchSteps + starts;
        Console.WriteLine(
            $"| {shape.Jobs} | {shape.Jobs * shape.OperationsPerJob} | {starts} | {budget} | {candidates} | {allocated} | " +
            $"{(double)allocated / candidates:F1} | {stopwatch.Elapsed.TotalMilliseconds:F1} | " +
            $"{stopwatch.Elapsed.TotalMilliseconds * 1000 / candidates:F1} |");
    }
}

// What the freed time buys on the one instance every published number is for:
// the same wall clock now covers several times the search it used to.
static void Budget()
{
    var shape = new Shape(100, 6, 10, 2, ProblemFactory.DefaultSeed);
    Console.WriteLine("| Acceptance | Starts | Budget/restart | Candidates | Penalty | ms |");
    Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");

    foreach (var acceptance in Enum.GetValues<LocalSearchAcceptance>())
    {
        foreach (int budget in new[] { 2_000, 6_000, 12_000, 20_000 })
        {
            var run = Measure(shape, budget, acceptance, starts: 8);
            Console.WriteLine(
                $"| {acceptance} | 8 | {budget} | {run.Steps + 8} | {run.Penalty:F3} | {run.Milliseconds:F1} |");
        }
    }
}

// The small-instance regime the size sweep under-samples: 7 jobs, a budget of
// 2 000 (many passes), and an exactly computable dispatch-order optimum to
// measure the gap against. This is where a restart is the only thing that can
// still move a converged descent.
static void Optimality()
{
    Console.WriteLine("| Acceptance | Starts | Instances | Mean gap | Worst gap | Over 5 % |");
    Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");

    foreach (var acceptance in Enum.GetValues<LocalSearchAcceptance>())
    {
        foreach (int starts in new[] { 1, 2, 4, 8 })
        {
            double gapSum = 0, worst = 0;
            int count = 0, over = 0;
            foreach (var rule in Enum.GetValues<DispatchRule>())
            {
                foreach (int seed in new[] { 1, 3, 7, 19, 42, 55, 101, 20260616 })
                {
                    var context = SevenJobInstance(seed, rule, acceptance, starts);
                    double found = new SchedulingEngine().Run(context).Evaluation.Penalty;
                    double optimum = ExhaustiveDispatchOrderSearch.Run(context).Result.Evaluation.Penalty;
                    double gap = optimum <= 0 ? 0 : (found - optimum) / optimum;

                    gapSum += gap;
                    worst = Math.Max(worst, gap);
                    if (gap > 0.05) over++;
                    count++;
                }
            }

            Console.WriteLine($"| {acceptance} | {starts} | {count} | {gapSum / count:P2} | {worst:P2} | {over} |");
        }
    }
}

// The same shape OptimalityTests generates, so the numbers explain that suite.
static SchedulingContext SevenJobInstance(int seed, DispatchRule rule, LocalSearchAcceptance acceptance, int starts)
{
    var rng = new DeterministicRandom(seed);
    var machines = Enumerable.Range(1, 4).Select(id => new MachineCapacity(id, $"WC-{id}")).ToArray();

    var jobs = new ProductionJob[7];
    for (int j = 0; j < jobs.Length; j++)
    {
        var steps = new List<JobStep>();
        int stepCount = 2 + rng.NextInt(3);
        for (int s = 0; s < stepCount; s++)
            steps.Add(new JobStep(s + 1, 1 + rng.NextInt(machines.Length), 600 + rng.NextInt(9000)));

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
        DispatchRule = rule,
        DueDateRule = DueDateRule.TotalWorkContent,
        TwkFlowFactor = 1.5,
        MultiStartRuns = starts,
        LocalSearchAcceptance = acceptance,
        Seed = seed
    });
}

// The study the documentation's optimality figures are supposed to come from.
// Every number in docs/adr/0015-exact-solver.md is a line of this output.
static void Exact()
{
    var summary = OptimalityStudy.Run();
    Console.WriteLine(OptimalityStudy.ToMarkdown(summary));
}

// Where the exponential wall is, measured rather than asserted. The instances are
// makespan-dominated job shops - target dates loose enough that nothing is late -
// because that is the hard case: with tight dates the job bound is often already
// the optimum and the search proves it in a few dozen nodes.
static void ExactWall()
{
    Console.WriteLine("| Jobs | Steps | WC | Ops | Status | Nodes | ms |");
    Console.WriteLine("| ---: | ---: | ---: | ---: | --- | ---: | ---: |");

    foreach (var (jobs, steps, centres) in WallShapes())
    {
        var context = WallInstance(jobs, steps, centres);
        var options = ExactSolverOptions.Default with
        {
            MaxOperations = 400,
            NodeLimit = 200_000_000,
            StateMemoCapacity = 1 << 20,
            TimeLimit = TimeSpan.FromSeconds(60)
        };

        Settle();
        var stopwatch = Stopwatch.StartNew();
        var solution = ExactJobShopSolver.Solve(context, options);
        stopwatch.Stop();

        Console.WriteLine(
            $"| {jobs} | {steps} | {centres} | {jobs * steps} | {solution.Status} | " +
            $"{solution.NodesExplored} | {stopwatch.Elapsed.TotalMilliseconds:F0} |");
    }
}

// Random routings over every work center, durations 10 to 50 minutes, and target
// dates three times the work content so the objective is the makespan alone.
static SchedulingContext WallInstance(int jobs, int steps, int centres, int variant = 0)
{
    var random = new DeterministicRandom(20260911 + jobs * 101 + steps * 17 + centres + variant * 7919);
    var machines = Enumerable.Range(1, centres)
        .Select(id => new MachineCapacity(id, $"WC-{id:00}"))
        .ToList();

    var list = new List<ProductionJob>(jobs);
    for (int index = 0; index < jobs; index++)
    {
        var routing = new List<JobStep>(steps);
        for (int step = 0; step < steps; step++)
        {
            int centre = (index + step * 2 + random.NextInt(centres)) % centres + 1;
            routing.Add(new JobStep((step + 1) * 10, centre, 600 + random.NextInt(9) * 300L));
        }

        list.Add(new ProductionJob
        {
            Id = index + 1,
            Reference = $"JOB-{index + 1:00}",
            Weight = 1,
            Steps = routing
        });
    }

    return new SchedulingContext(list, machines, new SchedulingParameters
    {
        DispatchRule = DispatchRule.EarliestDueDate,
        DueDateRule = DueDateRule.TotalWorkContent,
        TwkFlowFactor = 3.0,
        Seed = 20260911
    });
}

static (int Jobs, int Steps, int WorkCentres)[] WallShapes() =>
[
    (3, 3, 3), (4, 3, 3), (5, 3, 3), (6, 3, 3), (7, 3, 3), (8, 3, 3), (9, 3, 3), (10, 3, 3),
    (4, 4, 3), (5, 4, 3), (6, 4, 3), (7, 4, 3),
    (5, 4, 4), (6, 4, 4), (7, 4, 4), (8, 4, 4),
    (6, 5, 5), (7, 5, 5), (8, 5, 5)
];

// How often the solver proves an optimum inside one second, and inside one
// minute, over five instances of each size from the hard family. Wall-clock
// limits, so this mode is the one measurement here that does not reproduce
// exactly; the node counts do. It stops at 24 operations because that is already
// past the wall: two of five instances at 21 and at 24 are not proved in a
// minute, and every further size costs a minute per unproved instance.
static void ExactBudget()
{
    Console.WriteLine("| Jobs | Steps | WC | Ops | Proved <= 1 s | Proved <= 60 s | Median nodes when proved |");
    Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

    for (int jobs = 4; jobs <= 8; jobs++)
    {
        int fast = 0;
        int slow = 0;
        var nodes = new List<long>();

        for (int seed = 0; seed < 5; seed++)
        {
            var context = WallInstance(jobs, 3, 3, seed);
            var quick = Solve(context, TimeSpan.FromSeconds(1));
            if (quick.Status == ExactSolutionStatus.Optimal)
            {
                fast++;
                slow++;
                nodes.Add(quick.NodesExplored);
                continue;
            }

            var patient = Solve(context, TimeSpan.FromSeconds(60));
            if (patient.Status == ExactSolutionStatus.Optimal)
            {
                slow++;
                nodes.Add(patient.NodesExplored);
            }
        }

        nodes.Sort();
        string median = nodes.Count == 0 ? "-" : nodes[nodes.Count / 2].ToString();
        Console.WriteLine($"| {jobs} | 3 | 3 | {jobs * 3} | {fast}/5 | {slow}/5 | {median} |");
    }

    static ExactSolution Solve(SchedulingContext context, TimeSpan limit)
    {
        Settle();
        return ExactJobShopSolver.Solve(context, ExactSolverOptions.Default with
        {
            MaxOperations = 400,
            NodeLimit = 500_000_000,
            StateMemoCapacity = 1 << 20,
            TimeLimit = limit
        });
    }
}

// One LP file per study instance, for a reviewer who would rather trust HiGHS.
static void Milp(string directory)
{
    Directory.CreateDirectory(directory);
    Console.WriteLine("| Instance | Written | Reason |");
    Console.WriteLine("| --- | --- | --- |");

    foreach (var instance in OptimalityInstances.All())
    {
        if (!MilpModelWriter.CanWrite(instance.Context, out string reason))
        {
            Console.WriteLine($"| `{instance.Name}` | no | {reason} |");
            continue;
        }

        string path = Path.Combine(directory, $"{instance.Name}.lp");
        File.WriteAllText(path, MilpModelWriter.Write(instance.Context));
        Console.WriteLine($"| `{instance.Name}` | {path} | |");
    }
}

static void Explain()
{
    var context = ProblemFactory.Build(jobs: 100, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart: 8, localSearch: 2_000);
    var result = new SchedulingEngine().Run(context);
    _ = ScheduleExplainer.Explain(context, result);

    Console.WriteLine("| Probe | ms | Allocated MB |");
    Console.WriteLine("| --- | ---: | ---: |");
    foreach (bool probe in new[] { true, false })
    {
        Settle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        _ = ScheduleExplainer.Explain(context, result, probe);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"| {probe} | {stopwatch.Elapsed.TotalMilliseconds:F1} | {allocated / 1024d / 1024d:F2} |");
    }
}

static SchedulingContext Context(Shape shape, int budget, LocalSearchAcceptance acceptance, int starts) =>
    ProblemFactory.Build(
        shape.Jobs, shape.OperationsPerJob, shape.WorkCenters, shape.Capacity,
        multiStart: starts, localSearch: budget, acceptance: acceptance, seed: shape.Seed, variant: shape.Variant);

static Run Measure(Shape shape, int budget, LocalSearchAcceptance acceptance, int starts = 1)
{
    var context = Context(shape, budget, acceptance, starts);
    _ = new SchedulingEngine().Run(context);

    Settle();
    long before = GC.GetAllocatedBytesForCurrentThread();
    var stopwatch = Stopwatch.StartNew();
    var result = new SchedulingEngine().Run(context);
    stopwatch.Stop();

    return new Run(
        result.Evaluation.Penalty,
        result.LocalSearchSteps,
        stopwatch.Elapsed.TotalMilliseconds,
        GC.GetAllocatedBytesForCurrentThread() - before);
}

static void Settle()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

// n = 5, 8, 20, 50, 100, each with its own seed so the rows are five different
// instances rather than five prefixes of one.
// Five different instances per size — different routings, durations and
// releases, not one instance under five PRNG seeds. A difference that survives
// all five is not an artefact of the one instance the audit measured.
static int[] Seeds() => [0, 1, 2, 3, 4];

static Shape[] Shapes() =>
[
    new(5, 4, 3, 1, 11),
    new(8, 4, 4, 1, 22),
    new(20, 5, 6, 2, 33),
    new(50, 6, 8, 2, 44),
    new(100, 6, 10, 2, ProblemFactory.DefaultSeed)
];

// One full pass is n*(n-1) neighbours; the budgets straddle it so the table shows
// what happens below and at a single pass, and the third row is the budget the
// shipped default actually spends - which at small n is many passes, the regime
// where a descent has already converged and only a restart can move it.
static int[] Budgets(int jobs)
{
    int pass = jobs * (jobs - 1);
    return [Math.Max(20, pass / 4), pass, Math.Min(20_000, Math.Max(pass * 3, 2_000))];
}

internal readonly record struct Shape(int Jobs, int OperationsPerJob, int WorkCenters, int Capacity, int Seed, int Variant = 0);

internal readonly record struct Run(double Penalty, int Steps, double Milliseconds, long AllocatedBytes);

internal sealed record ScenarioDefinition(
    string Name,
    int Jobs,
    int OperationsPerJob,
    int WorkCenters,
    int Capacity,
    int MultiStart,
    int LocalSearch);
