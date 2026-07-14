using System.Diagnostics;
using WorkPlanStudio.Scheduling;

var scenarios = new[]
{
    new ScenarioDefinition("small", Jobs: 25, OperationsPerJob: 4, WorkCenters: 5, Capacity: 1, MultiStart: 4, LocalSearch: 500),
    new ScenarioDefinition("medium", Jobs: 100, OperationsPerJob: 6, WorkCenters: 10, Capacity: 2, MultiStart: 8, LocalSearch: 2_000),
    new ScenarioDefinition("large", Jobs: 250, OperationsPerJob: 8, WorkCenters: 20, Capacity: 2, MultiStart: 16, LocalSearch: 5_000)
};

Console.WriteLine($"Runtime: {Environment.Version}; OS: {Environment.OSVersion}; CPU: {Environment.ProcessorCount}");
Console.WriteLine();
Console.WriteLine("| Scenario | Jobs | Operations | Centers | Capacity | Starts | Local steps | Duration ms | Allocated MB | Peak working MB | Penalty | Deterministic |");
Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");

foreach (var definition in scenarios)
{
    var context = Build(definition);
    _ = new SchedulingEngine().Run(context); // warm JIT outside the measurement

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var stopwatch = Stopwatch.StartNew();
    var first = new SchedulingEngine().Run(context);
    stopwatch.Stop();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    var second = new SchedulingEngine().Run(context);

    Console.WriteLine(
        $"| {definition.Name} | {definition.Jobs} | {definition.Jobs * definition.OperationsPerJob} | " +
        $"{definition.WorkCenters} | {definition.Capacity} | {definition.MultiStart} | {definition.LocalSearch} | " +
        $"{stopwatch.Elapsed.TotalMilliseconds:F1} | {allocated / 1024d / 1024d:F2} | " +
        $"{Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d:F1} | {first.Evaluation.Penalty:F4} | " +
        $"{Signature(first) == Signature(second)} |");

    if (args.Contains("--verify", StringComparer.OrdinalIgnoreCase))
    {
        if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            throw new InvalidOperationException($"{definition.Name} exceeded the 10 second CI budget.");
        if (allocated > 512L * 1024 * 1024)
            throw new InvalidOperationException($"{definition.Name} exceeded the 512 MB allocation budget.");
        if (Signature(first) != Signature(second))
            throw new InvalidOperationException($"{definition.Name} was not deterministic.");
    }
}

static SchedulingContext Build(ScenarioDefinition definition)
{
    var machines = Enumerable.Range(1, definition.WorkCenters)
        .Select(id => new MachineCapacity(id, $"WC-{id:00}", definition.Capacity))
        .ToList();

    var jobs = Enumerable.Range(1, definition.Jobs)
        .Select(jobId => new ProductionJob
        {
            Id = jobId,
            Reference = $"JOB-{jobId:0000}",
            ReleaseSeconds = jobId % 7 * 60L,
            Weight = 1 + jobId % 10,
            Steps = Enumerable.Range(1, definition.OperationsPerJob)
                .Select(step => new JobStep(
                    step,
                    (jobId * 3 + step * 5) % definition.WorkCenters + 1,
                    60L + (jobId * 37L + step * 53L) % 3_600L))
                .ToList()
        })
        .ToList();

    return new SchedulingContext(jobs, machines, new SchedulingParameters
    {
        DispatchRule = DispatchRule.EarliestDueDate,
        DueDateRule = DueDateRule.TotalWorkContent,
        TwkFlowFactor = 2,
        MultiStartRuns = definition.MultiStart,
        LocalSearchMaxSteps = definition.LocalSearch,
        Seed = 20260712
    });
}

static string Signature(SchedulingResult result) => string.Join(
    '|',
    result.Schedule.Operations.Select(operation =>
        $"{operation.JobId}:{operation.StepNumber}:{operation.WorkCenterId}:{operation.SlotIndex}:{operation.StartSeconds}:{operation.EndSeconds}"));

internal sealed record ScenarioDefinition(
    string Name,
    int Jobs,
    int OperationsPerJob,
    int WorkCenters,
    int Capacity,
    int MultiStart,
    int LocalSearch);
