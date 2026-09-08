using BenchmarkDotNet.Attributes;
using WorkPlanStudio.Scheduling;

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
