using BenchmarkDotNet.Attributes;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Scheduling.Testing;

namespace WorkPlanStudio.Benchmarks;

/// <summary>
/// The engine on generated routing-shaped problems, from a single rule
/// dispatch to the optimiser with its default budget. The medium problem
/// (100 jobs × 6 steps on 10 centers) is the size the browser is expected to
/// handle interactively; "large" shows where the allocation curve bends.
/// </summary>
[MemoryDiagnoser]
public class SchedulingBenchmarks
{
    private SchedulingContext _small = null!;
    private SchedulingContext _medium = null!;
    private SchedulingContext _large = null!;
    private SchedulingContext _mediumRuleOnly = null!;
    private SchedulingContext _mediumWithCalendars = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = ProblemFactory.Build(jobs: 25, operationsPerJob: 4, workCenters: 5, capacity: 1, multiStart: 4, localSearch: 500);
        _medium = ProblemFactory.Build(jobs: 100, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart: 8, localSearch: 2_000);
        _large = ProblemFactory.Build(jobs: 250, operationsPerJob: 8, workCenters: 20, capacity: 2, multiStart: 16, localSearch: 5_000);
        _mediumRuleOnly = ProblemFactory.Build(jobs: 100, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart: 1, localSearch: 0);
        _mediumWithCalendars = ProblemFactory.Build(jobs: 100, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart: 8, localSearch: 2_000, withCalendars: true);
    }

    [Benchmark(Description = "small: 25 jobs, 4 starts, 500 steps")]
    public SchedulingResult Small() => new SchedulingEngine().Run(_small);

    [Benchmark(Description = "medium: 100 jobs, rule only")]
    public SchedulingResult MediumRuleOnly() => new SchedulingEngine().Run(_mediumRuleOnly);

    [Benchmark(Baseline = true, Description = "medium: 100 jobs, 8 starts, 2000 steps")]
    public SchedulingResult Medium() => new SchedulingEngine().Run(_medium);

    [Benchmark(Description = "medium + shift calendars, breaks, blackouts")]
    public SchedulingResult MediumWithCalendars() => new SchedulingEngine().Run(_mediumWithCalendars);

    [Benchmark(Description = "large: 250 jobs, 16 starts, 5000 steps")]
    public SchedulingResult Large() => new SchedulingEngine().Run(_large);
}

/// <summary>
/// The unit the whole search is built out of: scoring one candidate order. The
/// engine evaluates tens of thousands of these per run and keeps one, so this is
/// where both the time and the allocation budget are really spent — and the
/// memory column here is the number <c>docs/PERFORMANCE.md</c> should quote per
/// candidate, measured directly rather than divided out of a whole run.
/// <para>
/// <see cref="ScoreIntoWorkspace"/> is what local search calls;
/// <see cref="DispatchAndMaterialise"/> is the same dispatch plus the
/// <see cref="Schedule"/> object the engine now builds once per run rather than
/// once per candidate. The gap between the two rows is what the split bought.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class CandidateBenchmarks
{
    private SchedulingContext _context = null!;
    private SchedulingWorkspace _workspace = null!;
    private IReadOnlyDictionary<int, long> _dueByJob = null!;
    private int[] _order = null!;
    private readonly DispatchScheduler _scheduler = new();

    /// <summary>Jobs in the instance; 600 operations at 100 jobs is the documented medium problem.</summary>
    [Params(25, 100, 250)]
    public int Jobs { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = ProblemFactory.Build(Jobs, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart: 1, localSearch: 0);
        _dueByJob = DueDateAssigner.Assign(_context);
        _workspace = SchedulingWorkspace.For(_context, _dueByJob);
        _order = PriorityOrdering.For(_context, _dueByJob);
    }

    [Benchmark(Baseline = true, Description = "score one candidate into the workspace")]
    public ScheduleScore ScoreIntoWorkspace() => _scheduler.Score(_context, _order, _workspace);

    [Benchmark(Description = "dispatch and materialise a Schedule")]
    public Schedule DispatchAndMaterialise() => _scheduler.Run(_context, _order, _dueByJob);

    [Benchmark(Description = "score, then roll up every KPI")]
    public ScheduleEvaluation ScoreAndEvaluate()
    {
        var schedule = _scheduler.Run(_context, _order, _dueByJob);
        return ScheduleEvaluator.Evaluate(schedule, _context);
    }
}
