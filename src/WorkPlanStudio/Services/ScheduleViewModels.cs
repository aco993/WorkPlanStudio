using WorkPlanStudio.Scheduling;
using WorkPlanStudio.WorkingTime;

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

    /// <summary>
    /// The wall-clock moment second 0 of the schedule corresponds to (the
    /// earliest release), so the page can show real dates. Null without data.
    /// </summary>
    public DateTime? Horizon { get; init; }

    /// <summary>Total seconds all operations spent paused across breaks.</summary>
    public long TotalPausedSeconds { get; init; }

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

/// <summary>One Gantt lane: a work center, the bars placed on it and the time it was closed.</summary>
/// <param name="WorkCenterName">Display name.</param>
/// <param name="Bars">The operations placed on this work center.</param>
/// <param name="Closed">Stretches inside the makespan during which the work center was closed, with the reason.</param>
public sealed record GanttRow(string WorkCenterName, IReadOnlyList<GanttBar> Bars, IReadOnlyList<GanttClosedSegment> Closed)
{
    public GanttRow(string workCenterName, IReadOnlyList<GanttBar> bars) : this(workCenterName, bars, []) { }
}

/// <summary>A closed stretch of a Gantt lane: why the machine did not run then.</summary>
/// <param name="StartSeconds">Start, seconds from the horizon.</param>
/// <param name="EndSeconds">Exclusive end.</param>
/// <param name="Kind">Break, off-shift, Sunday, holiday, absence, …</param>
/// <param name="Label">The shift key, holiday key or absence label.</param>
public sealed record GanttClosedSegment(long StartSeconds, long EndSeconds, SegmentKind Kind, string Label)
{
    public long DurationSeconds => EndSeconds - StartSeconds;
}

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

    /// <summary>Seconds of the bar during which the operation waited for a break to end.</summary>
    public long PausedSeconds { get; init; }

    /// <summary>Seconds of change-over at the start of the bar.</summary>
    public long SetupSeconds { get; init; }
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
