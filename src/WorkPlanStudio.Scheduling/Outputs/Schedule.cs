using System.Text;

namespace WorkPlanStudio.Scheduling;

/// <summary>
/// A complete, feasible schedule: every operation placed on the timeline plus the
/// per-job outcome. Immutable — produced by <see cref="DispatchScheduler"/> and
/// scored by <see cref="ScheduleEvaluator"/>.
/// </summary>
public sealed class Schedule
{
    /// <summary>Every placed operation (unordered; query helpers below).</summary>
    public IReadOnlyList<ScheduledOperation> Operations { get; }

    /// <summary>The outcome for each job (completion versus target).</summary>
    public IReadOnlyList<JobSchedule> Jobs { get; }

    /// <summary>The moment the very last operation finishes; 0 for an empty schedule.</summary>
    public long MakespanSeconds { get; }

    /// <summary>Builds a schedule from its placed operations and per-job outcomes.</summary>
    public Schedule(IReadOnlyList<ScheduledOperation> operations, IReadOnlyList<JobSchedule> jobs)
    {
        Operations = operations;
        Jobs = jobs;
        MakespanSeconds = operations.Count == 0 ? 0L : operations.Max(o => o.EndSeconds);
    }

    /// <summary>Operations placed on a given work center, in start order.</summary>
    public IEnumerable<ScheduledOperation> OnWorkCenter(int workCenterId) =>
        Operations.Where(o => o.WorkCenterId == workCenterId).OrderBy(o => o.StartSeconds);

    /// <summary>
    /// A canonical, order-independent fingerprint of the placement. Two schedules
    /// with the same fingerprint are structurally identical — used by the
    /// determinism tests to assert reproducibility.
    /// </summary>
    public string Signature()
    {
        var sb = new StringBuilder();
        foreach (var op in Operations
                     .OrderBy(o => o.JobId)
                     .ThenBy(o => o.StepNumber)
                     .ThenBy(o => o.WorkCenterId))
        {
            sb.Append(op.JobId).Append(':').Append(op.StepNumber)
              .Append('@').Append(op.WorkCenterId).Append('#').Append(op.SlotIndex)
              .Append('[').Append(op.StartSeconds).Append('-').Append(op.EndSeconds).Append(']')
              .Append(';');
        }
        return sb.ToString();
    }
}
