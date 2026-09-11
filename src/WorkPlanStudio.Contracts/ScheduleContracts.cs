namespace WorkPlanStudio.Contracts;

/// <summary>
/// The scheduling parameters, flattened for the wire. Every field mirrors one
/// knob of the engine's <c>SchedulingParameters</c>; enums travel as their
/// integer values so that adding a rule on one side does not silently rename a
/// value on the other. All fields are optional and fall back to the engine's own
/// defaults, so a caller that only wants "schedule it" can post <c>{}</c>.
/// </summary>
public sealed record ScheduleRunRequest
{
    /// <summary>How competing jobs are prioritised on a shared work center.</summary>
    public int? DispatchRule { get; init; }

    /// <summary>How each job's target completion date is assigned.</summary>
    public int? DueDateRule { get; init; }

    /// <summary>TWK flow factor, dimensionless.</summary>
    public double? TwkFlowFactor { get; init; }

    /// <summary>NOP allowance per operation, in seconds.</summary>
    public long? NopSecondsPerOp { get; init; }

    /// <summary>SLK constant slack on top of total processing, in seconds.</summary>
    public long? SlackSeconds { get; init; }

    /// <summary>CON constant allowance from release to due, in seconds.</summary>
    public long? ConstantAllowanceSeconds { get; init; }

    /// <summary>Number of multi-start runs.</summary>
    public int? MultiStartRuns { get; init; }

    /// <summary>Upper bound on local-search neighbour evaluations.</summary>
    public int? LocalSearchMaxSteps { get; init; }

    /// <summary>Seed for the deterministic PRNG.</summary>
    public int? Seed { get; init; }

    /// <summary>Weight on makespan.</summary>
    public double? MakespanWeight { get; init; }

    /// <summary>Weight on total tardiness.</summary>
    public double? TardinessWeight { get; init; }

    /// <summary>Flat penalty per late job.</summary>
    public double? LatePenalty { get; init; }

    /// <summary>Working minutes per calendar day, used only to map work time onto days for display.</summary>
    public int? MinutesPerWorkingDay { get; init; }
}

/// <summary>Headline figures of a run.</summary>
/// <param name="MakespanSeconds">When the last operation finishes.</param>
/// <param name="OnTimeRate">Share of jobs that met their target, 0..1.</param>
/// <param name="TotalTardinessSeconds">Sum of every job's tardiness.</param>
/// <param name="AverageUtilization">Mean utilisation of the work centers used, 0..1.</param>
/// <param name="LateJobCount">How many jobs missed their target.</param>
/// <param name="JobCount">How many jobs were scheduled.</param>
public sealed record ScheduleKpisDto(
    long MakespanSeconds,
    double OnTimeRate,
    long TotalTardinessSeconds,
    double AverageUtilization,
    int LateJobCount,
    int JobCount);

/// <summary>One placed operation on a Gantt lane.</summary>
/// <param name="JobId">The production order.</param>
/// <param name="JobReference">Its order number.</param>
/// <param name="ColorIndex">Stable colour index, shared between chart and table.</param>
/// <param name="StepNumber">Position of this step within the job.</param>
/// <param name="StartSeconds">Start, seconds from the horizon.</param>
/// <param name="EndSeconds">Exclusive end.</param>
/// <param name="IsLate">Whether the owning job misses its target.</param>
/// <param name="PausedSeconds">Seconds of the bar spent waiting for a break to end.</param>
/// <param name="SetupSeconds">Seconds of change-over at the start of the bar.</param>
public sealed record GanttBarDto(
    int JobId,
    string JobReference,
    int ColorIndex,
    int StepNumber,
    long StartSeconds,
    long EndSeconds,
    bool IsLate,
    long PausedSeconds,
    long SetupSeconds);

/// <summary>A stretch in which a work center was closed, and why.</summary>
/// <param name="StartSeconds">Start, seconds from the horizon.</param>
/// <param name="EndSeconds">Exclusive end.</param>
/// <param name="Kind">The working-time segment kind (break, off-shift, Sunday, holiday, absence …).</param>
/// <param name="Label">Shift key, holiday key or absence label.</param>
public sealed record GanttClosedSegmentDto(long StartSeconds, long EndSeconds, int Kind, string Label);

/// <summary>One Gantt lane.</summary>
/// <param name="WorkCenterName">Display name of the work center.</param>
/// <param name="Bars">The operations placed on it.</param>
/// <param name="Closed">The closed stretches inside the makespan.</param>
public sealed record GanttRowDto(
    string WorkCenterName,
    IReadOnlyList<GanttBarDto> Bars,
    IReadOnlyList<GanttClosedSegmentDto> Closed);

/// <summary>A row of the per-job results table.</summary>
/// <param name="JobId">The production order.</param>
/// <param name="Reference">Its order number.</param>
/// <param name="PartName">The part being made.</param>
/// <param name="ColorIndex">Stable colour index.</param>
/// <param name="DueSeconds">Target completion, seconds from the horizon.</param>
/// <param name="CompletionSeconds">Actual completion.</param>
/// <param name="LatenessSeconds">Completion minus target; negative means early.</param>
/// <param name="IsLate">Whether the job missed its target.</param>
public sealed record JobRowDto(
    int JobId,
    string Reference,
    string PartName,
    int ColorIndex,
    long DueSeconds,
    long CompletionSeconds,
    long LatenessSeconds,
    bool IsLate);

/// <summary>A production order the mapper refused to schedule, with a stable reason code.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="OrderReference">Its order number.</param>
/// <param name="OperationNumber">The offending operation, when the problem is one step.</param>
/// <param name="Code">The reason code; the client localizes it.</param>
/// <param name="WorkCenterReference">The work center involved, when there is one.</param>
public sealed record SchedulePreparationIssueDto(
    int OrderId,
    string OrderReference,
    int? OperationNumber,
    int Code,
    string? WorkCenterReference);

/// <summary>The language-neutral explanation of a run.</summary>
/// <param name="JobCount">Jobs scheduled.</param>
/// <param name="OnTimeCount">Jobs that met their target.</param>
/// <param name="MakespanSeconds">When the last operation finishes.</param>
/// <param name="TotalTardinessSeconds">Sum of every job's tardiness.</param>
/// <param name="AverageUtilization">Mean utilisation, 0..1.</param>
/// <param name="BottleneckWorkCenterId">The busiest work center, or null when nothing ran.</param>
/// <param name="BottleneckWorkCenterName">Its display name.</param>
/// <param name="BottleneckUtilization">Its utilisation, 0..1.</param>
/// <param name="BottleneckOperationCount">How many operations it carried.</param>
/// <param name="LateJobs">The late jobs, most tardy first.</param>
/// <param name="RecommendationKind">0 already on time, 1 switch dispatch rule, 2 no improvement found.</param>
/// <param name="CurrentRule">The dispatch rule that produced this schedule.</param>
/// <param name="SuggestedRule">The rule to try next, when one measurably wins.</param>
/// <param name="CurrentTardinessSeconds">Total tardiness now.</param>
/// <param name="ProjectedTardinessSeconds">Total tardiness projected under <paramref name="SuggestedRule"/>.</param>
public sealed record ScheduleExplanationDto(
    int JobCount,
    int OnTimeCount,
    long MakespanSeconds,
    long TotalTardinessSeconds,
    double AverageUtilization,
    int? BottleneckWorkCenterId,
    string? BottleneckWorkCenterName,
    double BottleneckUtilization,
    int BottleneckOperationCount,
    IReadOnlyList<LateJobFindingDto> LateJobs,
    int RecommendationKind,
    int CurrentRule,
    int? SuggestedRule,
    long CurrentTardinessSeconds,
    long ProjectedTardinessSeconds);

/// <summary>Why one job missed its target.</summary>
/// <param name="JobId">The job.</param>
/// <param name="JobReference">Its order number.</param>
/// <param name="TardinessSeconds">How late it finished.</param>
/// <param name="QueueWaitSeconds">Total time spent waiting for a machine.</param>
/// <param name="BlockingWorkCenterName">The work center it waited on longest, or null when it never queued.</param>
public sealed record LateJobFindingDto(
    int JobId,
    string JobReference,
    long TardinessSeconds,
    long QueueWaitSeconds,
    string? BlockingWorkCenterName);

/// <summary>
/// A complete run, in the shape the scheduling page renders.
/// <para>
/// <see cref="Signature"/> is the engine's own canonical fingerprint of the
/// placement. It is on the wire so that a caller can prove the server produced
/// the same schedule the browser would have: same input, same signature, on two
/// different hosts and two different runtimes.
/// </para>
/// </summary>
/// <param name="HasData">False when nothing could be scheduled.</param>
/// <param name="Kpis">Headline figures.</param>
/// <param name="Rows">The Gantt lanes.</param>
/// <param name="Jobs">The per-job table.</param>
/// <param name="MakespanSeconds">When the last operation finishes.</param>
/// <param name="MinutesPerWorkingDay">Display-only day length, echoed from the request.</param>
/// <param name="LocalSearchSteps">Neighbours evaluated during the polish phase.</param>
/// <param name="HorizonUtc">The wall-clock moment second 0 corresponds to; null without data.</param>
/// <param name="TotalPausedSeconds">Total seconds all operations spent paused across breaks.</param>
/// <param name="UtilizationByWorkCenter">Utilisation per lane, keyed by display name.</param>
/// <param name="EquivalentRules">Dispatch rules that would have produced this exact job order.</param>
/// <param name="PreparationErrors">Orders excluded before the engine ran.</param>
/// <param name="Explanation">The computed explanation, or null when there is nothing to explain.</param>
/// <param name="Signature">Canonical fingerprint of the placement.</param>
public sealed record ScheduleRunResponse(
    bool HasData,
    ScheduleKpisDto Kpis,
    IReadOnlyList<GanttRowDto> Rows,
    IReadOnlyList<JobRowDto> Jobs,
    long MakespanSeconds,
    int MinutesPerWorkingDay,
    int LocalSearchSteps,
    DateTime? HorizonUtc,
    long TotalPausedSeconds,
    IReadOnlyDictionary<string, double> UtilizationByWorkCenter,
    IReadOnlyList<int> EquivalentRules,
    IReadOnlyList<SchedulePreparationIssueDto> PreparationErrors,
    ScheduleExplanationDto? Explanation,
    string Signature);
