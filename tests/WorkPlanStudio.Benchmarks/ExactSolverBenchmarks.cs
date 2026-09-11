using BenchmarkDotNet.Attributes;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Scheduling.Exact;
using WorkPlanStudio.Scheduling.Testing;

namespace WorkPlanStudio.Benchmarks;

/// <summary>
/// The exact solver on instances from the optimality study, so the cost of
/// proving an optimum sits next to the cost of guessing at one.
/// <para>
/// The interesting column is <c>Allocated</c>: the search itself allocates
/// nothing per node, and everything the memory diagnoser reports is the state
/// memo. Turning the memo off is in the table for exactly that reason — it costs
/// almost no memory and roughly twenty-four times the nodes.
/// </para>
/// <para>
/// These are seconds, not microseconds. Job-shop scheduling is NP-hard and the
/// point of the benchmark is to show where the wall is, not to pretend there is
/// not one — <c>docs/adr/0015-exact-solver.md</c> has the size sweep.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class ExactSolverBenchmarks
{
    private SchedulingContext _small = null!;
    private SchedulingContext _medium = null!;
    private SchedulingContext _parallel = null!;
    private SchedulingContext _large = null!;
    private SchedulingContext _milp = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = Instance("jobshop-3x3");
        _medium = Instance("jobshop-5x3");
        _parallel = Instance("parallel-6x2-cap2");
        _large = Instance("jobshop-7x3");
        _milp = Instance("bottleneck-6x2");
    }

    private static SchedulingContext Instance(string name)
    {
        foreach (var instance in OptimalityInstances.All())
        {
            if (instance.Name == name)
                return instance.Context;
        }

        throw new InvalidOperationException($"No study instance named '{name}'.");
    }

    [Benchmark(Baseline = true, Description = "prove 9 operations optimal")]
    public ExactSolution Small() => ExactJobShopSolver.Solve(_small);

    [Benchmark(Description = "prove 15 operations optimal")]
    public ExactSolution Medium() => ExactJobShopSolver.Solve(_medium);

    [Benchmark(Description = "prove 15 operations optimal, no state memo")]
    public ExactSolution MediumWithoutMemo() =>
        ExactJobShopSolver.Solve(_medium, ExactSolverOptions.Default with { UseStateMemo = false });

    [Benchmark(Description = "prove 15 operations optimal, no edge finding")]
    public ExactSolution MediumWithoutEdgeFinding() =>
        ExactJobShopSolver.Solve(_medium, ExactSolverOptions.Default with { UseEdgeFinding = false });

    [Benchmark(Description = "prove 12 operations on two-slot work centers optimal")]
    public ExactSolution Parallel() => ExactJobShopSolver.Solve(_parallel);

    [Benchmark(Description = "prove 21 operations optimal")]
    public ExactSolution Large() => ExactJobShopSolver.Solve(_large);

    [Benchmark(Description = "write the LP model for 12 operations")]
    public string Milp() => MilpModelWriter.Write(_milp);
}
