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

    /// <summary>The outcome for each job, ordered by <see cref="JobSchedule.JobId"/>.</summary>
    /// <remarks>
    /// The order is part of the contract because this is what a UI binds a table
    /// to. It used to be dispatch order, so two runs that found the identical
    /// schedule from different starting points still reordered the rows.
    /// </remarks>
    public IReadOnlyList<JobSchedule> Jobs { get; }

    /// <summary>The moment the very last operation finishes; 0 for an empty schedule.</summary>
    public long MakespanSeconds { get; }

    /// <summary>Builds a schedule from its placed operations and per-job outcomes.</summary>
    /// <remarks>
    /// Both collections are copied. <see cref="MakespanSeconds"/> is computed
    /// eagerly, so a caller that kept its own list and mutated it afterwards would
    /// otherwise leave a public property describing data the schedule no longer
    /// holds.
    /// </remarks>
    public Schedule(IReadOnlyList<ScheduledOperation> operations, IReadOnlyList<JobSchedule> jobs)
        : this(Copy(operations), Copy(jobs), sorted: false)
    {
    }

    private Schedule(ScheduledOperation[] operations, JobSchedule[] jobs, bool sorted)
    {
        if (!sorted)
            Array.Sort(jobs, static (a, b) => a.JobId.CompareTo(b.JobId));

        Operations = operations;
        Jobs = jobs;

        // A for loop rather than Operations.Max(o => o.EndSeconds): the LINQ form
        // boxes an enumerator on every schedule built.
        long makespan = 0;
        for (int i = 0; i < operations.Length; i++)
        {
            if (operations[i].EndSeconds > makespan)
                makespan = operations[i].EndSeconds;
        }

        MakespanSeconds = makespan;
    }

    /// <summary>Takes ownership of arrays the caller has just built and will not touch again.</summary>
    internal static Schedule FromOwnedArrays(ScheduledOperation[] operations, JobSchedule[] jobs) =>
        new(operations, jobs, sorted: true);

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

        // The per-job rows are part of the schedule a caller sees, so they are
        // part of what "structurally identical" has to mean; leaving them out is
        // what let the row order drift without any determinism test noticing.
        sb.Append('|');
        foreach (var job in Jobs)
        {
            sb.Append(job.JobId).Append(':').Append(job.DueSeconds)
              .Append('/').Append(job.CompletionSeconds).Append(';');
        }

        return sb.ToString();
    }

    private static T[] Copy<T>(IReadOnlyList<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new T[source.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = source[i];
        return copy;
    }
}
