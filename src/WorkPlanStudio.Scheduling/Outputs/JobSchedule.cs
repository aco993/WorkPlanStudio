namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The scheduling outcome for a single job: when it finishes versus its assigned
/// target date, and the derived lateness / tardiness. All times are seconds from
/// the horizon.
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="Reference">Display label.</param>
/// <param name="ReleaseSeconds">When the job became available.</param>
/// <param name="DueSeconds">The assigned target completion ("meta").</param>
/// <param name="CompletionSeconds">When the last step actually finishes.</param>
public sealed record JobSchedule(
    int JobId,
    string Reference,
    long ReleaseSeconds,
    long DueSeconds,
    long CompletionSeconds)
{
    /// <summary>Signed deviation from the target: positive = late, negative = early.</summary>
    public long LatenessSeconds => CompletionSeconds - DueSeconds;

    /// <summary>Lateness floored at zero — the part that actually hurts.</summary>
    public long TardinessSeconds => Math.Max(0, LatenessSeconds);

    /// <summary>True when the job finishes after its target date.</summary>
    public bool IsLate => CompletionSeconds > DueSeconds;

    /// <summary>Throughput time from release to completion.</summary>
    public long FlowSeconds => CompletionSeconds - ReleaseSeconds;
}
