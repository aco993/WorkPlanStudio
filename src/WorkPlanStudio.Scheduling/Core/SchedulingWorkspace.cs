namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Reusable scratch space for one search: the slot clocks, the placed operations
/// and the per-job completions, all preallocated from the context's shape.
/// <para>
/// A dispatch used to allocate roughly 67 KB — 600 <c>ScheduledOperation</c>
/// objects, two growing lists and a dictionary of slot-state objects — and the
/// search discards all of it unless the candidate happens to become the
/// incumbent. One workspace per search turns that into zero bytes per candidate:
/// the arrays are sized once, cleared in two <c>Array.Clear</c> calls, and
/// overwritten in place.
/// </para>
/// <para>
/// Not thread-safe by design. A workspace belongs to exactly one search; two
/// concurrent searches need two workspaces.
/// </para>
/// </summary>
public sealed class SchedulingWorkspace
{
    internal long[] SlotFreeAt { get; }
    internal int[] SlotFamilyId { get; }
    internal OperationRecord[] Operations { get; }
    internal long[] CompletionByJobIndex { get; }
    internal long[] DueByJobIndex { get; }
    internal int OperationCount { get; set; }

    private SchedulingWorkspace(int slots, int steps, int jobs)
    {
        SlotFreeAt = new long[slots];
        SlotFamilyId = new int[slots];
        Operations = new OperationRecord[steps];
        CompletionByJobIndex = new long[jobs];
        DueByJobIndex = new long[jobs];
    }

    /// <summary>
    /// Allocates a workspace for <paramref name="context"/>, resolving each job's
    /// target date from <paramref name="dueByJob"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A job has no entry in <paramref name="dueByJob"/>.</exception>
    /// <remarks>
    /// The target dates are resolved here, once, rather than looked up per job per
    /// candidate — and a missing one is an error rather than a silent default. The
    /// old fallback was the job's own completion time, which makes the job
    /// on time by construction and worth nothing to the objective, so the search
    /// would preferentially starve exactly the job whose target went missing.
    /// </remarks>
    public static SchedulingWorkspace For(SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dueByJob);

        var workspace = new SchedulingWorkspace(context.TotalSlots, context.TotalSteps, context.Jobs.Count);
        for (int j = 0; j < context.Jobs.Count; j++)
        {
            var job = context.Jobs[j];
            if (!dueByJob.TryGetValue(job.Id, out long due))
                throw new ArgumentException($"No target date for job {job.Id} ('{job.Reference}').", nameof(dueByJob));
            workspace.DueByJobIndex[j] = due;
        }

        return workspace;
    }

    /// <summary>True when this workspace was sized for <paramref name="context"/>.</summary>
    internal bool Fits(SchedulingContext context) =>
        SlotFreeAt.Length == context.TotalSlots &&
        Operations.Length == context.TotalSteps &&
        CompletionByJobIndex.Length == context.Jobs.Count;

    /// <summary>Clears the slot clocks so the next dispatch starts from an empty shop.</summary>
    internal void Reset()
    {
        Array.Clear(SlotFreeAt);
        SlotFamilyId.AsSpan().Fill(-1);
        OperationCount = 0;
    }
}

/// <summary>
/// One placed operation inside a workspace, addressed by index rather than by
/// object reference. A struct in a preallocated array, so a dispatch places 600
/// operations without a single heap allocation; the public
/// <see cref="ScheduledOperation"/> is projected from these once, for the schedule
/// that is actually kept.
/// </summary>
/// <param name="JobIndex">Index into <see cref="SchedulingContext.Jobs"/>.</param>
/// <param name="StepIndex">Index into the context's flattened step arrays.</param>
/// <param name="SlotIndex">Which parallel slot of the work center (0-based).</param>
/// <param name="StartSeconds">Start time in seconds from the horizon.</param>
/// <param name="EndSeconds">End time in seconds from the horizon.</param>
/// <param name="SetupSeconds">Change-over charged inside the placement.</param>
/// <param name="PausedSeconds">Closed time the operation waited across.</param>
internal readonly record struct OperationRecord(
    int JobIndex,
    int StepIndex,
    int SlotIndex,
    long StartSeconds,
    long EndSeconds,
    long SetupSeconds,
    long PausedSeconds);
