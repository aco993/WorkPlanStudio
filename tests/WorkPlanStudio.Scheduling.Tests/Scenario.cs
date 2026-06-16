namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>Concise builders for scheduling test fixtures, imported statically.</summary>
internal static class Scenario
{
    public static MachineCapacity Machine(int id, int capacity = 1) => new(id, $"WC-{id}", capacity);

    public static JobStep Step(int step, int workCenterId, long seconds) => new(step, workCenterId, seconds);

    public static ProductionJob Job(int id, params JobStep[] steps) => Make(id, 0, 1.0, null, steps);
    public static ProductionJob Released(int id, long releaseSeconds, params JobStep[] steps) => Make(id, releaseSeconds, 1.0, null, steps);
    public static ProductionJob Weighted(int id, double weight, params JobStep[] steps) => Make(id, 0, weight, null, steps);
    public static ProductionJob DueAt(int id, long dueSeconds, params JobStep[] steps) => Make(id, 0, 1.0, dueSeconds, steps);

    private static ProductionJob Make(int id, long release, double weight, long? due, JobStep[] steps) => new()
    {
        Id = id,
        Reference = $"J{id}",
        ReleaseSeconds = release,
        Weight = weight,
        ExplicitDueSeconds = due,
        Steps = steps
    };

    public static SchedulingContext Context(
        SchedulingParameters parameters, IReadOnlyList<MachineCapacity> machines, params ProductionJob[] jobs)
        => new(jobs, machines, parameters);

    /// <summary>Parameters that isolate the dispatch rule: a single start with no local search.</summary>
    public static SchedulingParameters RuleOnly(DispatchRule rule, DueDateRule due = DueDateRule.TotalWorkContent) => new()
    {
        DispatchRule = rule,
        DueDateRule = due,
        MultiStartRuns = 1,
        LocalSearchMaxSteps = 0
    };
}
