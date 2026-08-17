using Microsoft.Extensions.Localization;

namespace WorkPlanStudio.Web.Tests;

/// <summary>A test double for the scheduling service: returns a canned result and records the call.</summary>
internal sealed class FakeScheduleService : IProductionScheduleService
{
    public ScheduleResult Result { get; set; } = ScheduleResult.Empty(480);
    public SchedulingParameters? LastParameters { get; private set; }
    public int Calls { get; private set; }
    public Exception? ExceptionToThrow { get; set; }

    public Task<ScheduleResult> GenerateAsync(SchedulingParameters parameters, CancellationToken cancellationToken = default)
    {
        LastParameters = parameters;
        Calls++;
        if (ExceptionToThrow is not null)
            return Task.FromException<ScheduleResult>(ExceptionToThrow);
        return Task.FromResult(Result);
    }
}

/// <summary>A test double for the assistant config that returns fixed settings.</summary>
internal sealed class FakeAssistantConfig : IAssistantConfig
{
    public AssistantSettings Settings { get; set; } = AssistantSettings.Default;

    public ValueTask<AssistantSettings> LoadAsync() => ValueTask.FromResult(Settings);

    public Task SaveAsync(AssistantSettings settings)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A localizer that echoes the resource key. Component tests assert on structure
/// and keys, not on translated copy, so this keeps them independent of the .resx.
/// </summary>
internal sealed class PassThroughLocalizer<T> : IStringLocalizer<T>
{
    public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
    public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments), false);
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}

/// <summary>
/// Locates the repository from the test binary, for the handful of tests that
/// assert on sources rather than on a render.
/// </summary>
internal static class RepoFiles
{
    public static string Root { get; } = Find();

    public static string AppWwwroot => Path.Join(Root, "src", "WorkPlanStudio", "wwwroot");

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "src")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Hand-built <see cref="ScheduleResult"/>s for component tests.</summary>
internal static class Sample
{
    public static ScheduleResult OnTime() => new(
        HasData: true,
        Kpis: new ScheduleKpis(MakespanSeconds: 1200, OnTimeRate: 1.0, TotalTardinessSeconds: 0, AverageUtilization: 0.8, LateJobCount: 0, JobCount: 2),
        Rows:
        [
            new GanttRow("SAW-10 — Cut-off Saw", [new GanttBar(1, "WP-1", 0, 1, 0, 600, IsLate: false)]),
            new GanttRow("CNC-200 — Turning",    [new GanttBar(2, "WP-2", 1, 1, 600, 1200, IsLate: false)]),
        ],
        Jobs:
        [
            new JobRow(1, "WP-1", "Drive shaft", 0, DueSeconds: 5000, CompletionSeconds: 600, LatenessSeconds: -4400, IsLate: false),
            new JobRow(2, "WP-2", "Bracket",     1, DueSeconds: 5000, CompletionSeconds: 1200, LatenessSeconds: -3800, IsLate: false),
        ],
        MakespanSeconds: 1200, MinutesPerWorkingDay: 480, LocalSearchSteps: 0)
    {
        Explanation = new ScheduleExplanation(
            new ScheduleSummary(JobCount: 2, OnTimeCount: 2, MakespanSeconds: 1200, TotalTardinessSeconds: 0, AverageUtilization: 0.8),
            new BottleneckFinding(1, "SAW-10 — Cut-off Saw", 0.8, 1),
            [],
            new ScheduleRecommendation(RecommendationKind.AlreadyOnTime, DispatchRule.EarliestDueDate, null, 0, 0))
    };

    public static ScheduleResult WithLateJob() => new(
        HasData: true,
        Kpis: new ScheduleKpis(MakespanSeconds: 600, OnTimeRate: 0.0, TotalTardinessSeconds: 300, AverageUtilization: 1.0, LateJobCount: 1, JobCount: 1),
        Rows: [new GanttRow("SAW-10 — Cut-off Saw", [new GanttBar(1, "WP-1", 0, 1, 0, 600, IsLate: true)])],
        Jobs: [new JobRow(1, "WP-1", "Drive shaft", 0, DueSeconds: 300, CompletionSeconds: 600, LatenessSeconds: 300, IsLate: true)],
        MakespanSeconds: 600, MinutesPerWorkingDay: 480, LocalSearchSteps: 0)
    {
        Explanation = new ScheduleExplanation(
            new ScheduleSummary(JobCount: 1, OnTimeCount: 0, MakespanSeconds: 600, TotalTardinessSeconds: 300, AverageUtilization: 1.0),
            new BottleneckFinding(1, "SAW-10 — Cut-off Saw", 1.0, 1),
            [new LateJobFinding(1, "WP-1", 300, 200, "SAW-10 — Cut-off Saw")],
            new ScheduleRecommendation(RecommendationKind.SwitchDispatchRule, DispatchRule.LongestProcessingTime, DispatchRule.ShortestProcessingTime, 300, 0))
    };
}
