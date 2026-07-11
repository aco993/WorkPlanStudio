namespace WorkPlanStudio.Scheduling;

/// <summary>Tone of a finding, so a UI can colour it without re-deriving meaning.</summary>
public enum FindingTone
{
    /// <summary>A positive result (e.g. everything on time).</summary>
    Good,

    /// <summary>Neutral, factual context.</summary>
    Info,

    /// <summary>Something that hurt the objective (late jobs, a hot bottleneck).</summary>
    Warning
}

/// <summary>The headline outcome of a scheduling run.</summary>
/// <param name="JobCount">Jobs scheduled.</param>
/// <param name="OnTimeCount">Jobs that met their target.</param>
/// <param name="MakespanSeconds">When the last operation finishes.</param>
/// <param name="TotalTardinessSeconds">Sum of every job's tardiness.</param>
/// <param name="AverageUtilization">Mean utilisation of the work centers used, 0..1.</param>
public sealed record ScheduleSummary(
    int JobCount,
    int OnTimeCount,
    long MakespanSeconds,
    long TotalTardinessSeconds,
    double AverageUtilization);

/// <summary>The most heavily loaded work center — the schedule's likely constraint.</summary>
/// <param name="WorkCenterId">The work center.</param>
/// <param name="WorkCenterName">Its display name (from the input data).</param>
/// <param name="Utilization">Busy ÷ available over the makespan, 0..1.</param>
/// <param name="OperationCount">How many operations were placed on it.</param>
public sealed record BottleneckFinding(
    int WorkCenterId,
    string WorkCenterName,
    double Utilization,
    int OperationCount);

/// <summary>Why one job missed its target date.</summary>
/// <param name="JobId">The job.</param>
/// <param name="JobReference">Its display label (e.g. the plan number).</param>
/// <param name="TardinessSeconds">How late it finished.</param>
/// <param name="QueueWaitSeconds">Total time the job spent waiting for a machine to free up.</param>
/// <param name="BlockingWorkCenterName">
/// The work center it waited on longest, or <c>null</c> when it never queued — in
/// which case the target itself was tighter than the job's own processing time.
/// </param>
public sealed record LateJobFinding(
    int JobId,
    string JobReference,
    long TardinessSeconds,
    long QueueWaitSeconds,
    string? BlockingWorkCenterName);

/// <summary>What the recommendation is telling the planner to do.</summary>
public enum RecommendationKind
{
    /// <summary>Every job is already on time — nothing to change.</summary>
    AlreadyOnTime,

    /// <summary>Another dispatch rule is expected to reduce total tardiness.</summary>
    SwitchDispatchRule,

    /// <summary>Jobs are late, but no alternative dispatch rule did better here.</summary>
    NoImprovementFound
}

/// <summary>
/// A single, deterministic, <b>computed</b> suggestion for the next run — not a
/// guess. When jobs are late, the explainer quickly re-dispatches the other rules
/// and only proposes a switch when one measurably beats the current result.
/// </summary>
/// <param name="Kind">Which kind of advice this is.</param>
/// <param name="CurrentRule">The dispatch rule that produced the current schedule.</param>
/// <param name="SuggestedRule">The rule to try next, when <see cref="Kind"/> is <see cref="RecommendationKind.SwitchDispatchRule"/>.</param>
/// <param name="CurrentTardinessSeconds">Total tardiness of the current schedule.</param>
/// <param name="ProjectedTardinessSeconds">Estimated total tardiness under <see cref="SuggestedRule"/>.</param>
public sealed record ScheduleRecommendation(
    RecommendationKind Kind,
    DispatchRule CurrentRule,
    DispatchRule? SuggestedRule,
    long CurrentTardinessSeconds,
    long ProjectedTardinessSeconds);

/// <summary>
/// A structured, <b>language-neutral</b> explanation of a schedule: what happened,
/// where the constraint is, which jobs are late and why, and one computed
/// suggestion for the next run. The app layer turns this into localized prose (or
/// feeds it to an optional AI narrator) — nothing here is UI- or language-specific,
/// and it is produced deterministically from the engine output.
/// </summary>
/// <param name="Summary">Headline KPIs.</param>
/// <param name="Bottleneck">The busiest work center, or <c>null</c> when nothing ran.</param>
/// <param name="LateJobs">The worst late jobs (most tardy first), possibly empty.</param>
/// <param name="Recommendation">One computed next step.</param>
public sealed record ScheduleExplanation(
    ScheduleSummary Summary,
    BottleneckFinding? Bottleneck,
    IReadOnlyList<LateJobFinding> LateJobs,
    ScheduleRecommendation Recommendation);
