using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>Everything the scheduling page needs to render one run.</summary>
public sealed record ScheduleResult(
    bool HasData,
    ScheduleKpis Kpis,
    IReadOnlyList<GanttRow> Rows,
    IReadOnlyList<JobRow> Jobs,
    long MakespanSeconds,
    int MinutesPerWorkingDay,
    int LocalSearchSteps)
{
    /// <summary>
    /// The structured, deterministic explanation of this run (bottleneck, late jobs,
    /// recommendation), or <c>null</c> when there is nothing to explain. Consumed by
    /// the assistant to produce the narration shown on the page.
    /// </summary>
    public ScheduleExplanation? Explanation { get; init; }

    /// <summary>Released plans excluded because their routing is invalid.</summary>
    public IReadOnlyList<SchedulePreparationIssue> PreparationErrors { get; init; } = [];

    /// <summary>
    /// Dispatch rules that would have produced this exact job order. Shown so a
    /// user who switches rules and sees no change understands why.
    /// </summary>
    public IReadOnlyList<DispatchRule> EquivalentRules { get; init; } = [];

    /// <summary>The result shown when there is nothing to schedule.</summary>
    public static ScheduleResult Empty(int minutesPerWorkingDay) =>
        new(false, new ScheduleKpis(0, 1, 0, 0, 0, 0), [], [], 0, minutesPerWorkingDay, 0);
}

/// <summary>Headline figures shown as KPI cards.</summary>
public sealed record ScheduleKpis(
    long MakespanSeconds,
    double OnTimeRate,
    long TotalTardinessSeconds,
    double AverageUtilization,
    int LateJobCount,
    int JobCount);

/// <summary>One Gantt lane: a work center and the bars placed on it.</summary>
public sealed record GanttRow(string WorkCenterName, IReadOnlyList<GanttBar> Bars);

/// <summary>A single Gantt bar. <see cref="IsLate"/> drives the danger styling.</summary>
public sealed record GanttBar(
    int JobId,
    string JobReference,
    int ColorIndex,
    int StepNumber,
    long StartSeconds,
    long EndSeconds,
    bool IsLate)
{
    public long DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>A row in the per-job results table.</summary>
public sealed record JobRow(
    int JobId,
    string Reference,
    string PartName,
    int ColorIndex,
    long DueSeconds,
    long CompletionSeconds,
    long LatenessSeconds,
    bool IsLate);
