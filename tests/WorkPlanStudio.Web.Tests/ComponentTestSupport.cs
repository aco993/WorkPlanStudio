using Microsoft.Extensions.Localization;
using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Web.Tests;

/// <summary>A test double for the scheduling service: returns a canned result and records the call.</summary>
internal sealed class FakeScheduleService : IProductionScheduleService
{
    public ScheduleResult Result { get; set; } = ScheduleResult.Empty(480);
    public SchedulingParameters? LastParameters { get; private set; }
    public int LastMinutesPerWorkingDay { get; private set; }
    public int Calls { get; private set; }
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// Progress values to report before returning, and a gate to hold the call open
    /// on, so a test can observe the page mid-run — press Cancel, read the
    /// indicator — instead of only before and after.
    /// </summary>
    public IReadOnlyList<ScheduleRunProgress> ProgressToReport { get; set; } = [];

    public TaskCompletionSource Gate { get; } = new();

    public bool UseGate { get; set; }

    public Task<ScheduleResult> GenerateAsync(SchedulingParameters parameters, CancellationToken cancellationToken = default) =>
        GenerateAsync(parameters, 480, null, cancellationToken);

    public async Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        int minutesPerWorkingDay,
        IProgress<ScheduleRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        LastParameters = parameters;
        LastMinutesPerWorkingDay = minutesPerWorkingDay;
        Calls++;

        foreach (var step in ProgressToReport)
            progress?.Report(step);

        if (UseGate)
            await Gate.Task.WaitAsync(cancellationToken);

        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        cancellationToken.ThrowIfCancellationRequested();
        return Result;
    }
}

/// <summary>A test double for the assistant config that returns fixed settings.</summary>
internal sealed class FakeAssistantConfig : IAssistantConfig
{
    public AssistantSettings Settings { get; set; } = AssistantSettings.Default;

    public ValueTask<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Settings);

    public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
    {
        // Same contract as the real store: a blank key means "keep the stored one".
        Settings = settings.HasApiKey ? settings : settings with { ApiKey = Settings.ApiKey };
        return Task.CompletedTask;
    }

    public Task ForgetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        Settings = Settings with { ApiKey = "" };
        return Task.CompletedTask;
    }

    /// <summary>
    /// The double keeps one provider's key, so this answers for the provider the
    /// settings currently name and reports "nothing stored" for any other — which
    /// is the behaviour the dialog has to cope with anyway.
    /// </summary>
    public ValueTask<bool> HasKeyForAsync(AssistantProvider provider, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(provider == Settings.Provider && Settings.HasApiKey);
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

internal static class ExportTestSupport
{
    /// <summary>
    /// The three services the scheduling page's export control resolves. They
    /// live here rather than being repeated in every arrangement: the page owns
    /// the control, so every test that renders the page needs them, and a new
    /// one should not have to find that out from a render-time exception.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddScheduleExport(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<ScheduleExportBuilder>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<FileDownloadService>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<ScheduleExportService>(services);
        return services;
    }
}

/// <summary>A persona store that never touches the browser.</summary>
internal sealed class FakePersonaStore : WorkPlanStudio.Services.Auth.IPersonaStore
{
    public WorkPlanStudio.Services.Auth.WorkspaceRole? Stored { get; set; }

    public ValueTask<WorkPlanStudio.Services.Auth.WorkspaceRole?> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Stored);

    public ValueTask SaveAsync(WorkPlanStudio.Services.Auth.WorkspaceRole role, CancellationToken cancellationToken = default)
    {
        Stored = role;
        return ValueTask.CompletedTask;
    }
}

internal static class AuthorizationTestSupport
{
    /// <summary>
    /// Registers the real authorization pipeline (policies, AuthorizeView's
    /// cascading state, the service guard) with a persona from a fake store, so
    /// component tests exercise the same code the app runs.
    /// </summary>
    public static WorkPlanStudio.Services.Auth.DemoAuthenticationStateProvider AddDemoAuthorization(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        WorkPlanStudio.Services.Auth.WorkspaceRole role)
    {
        var provider = new WorkPlanStudio.Services.Auth.DemoAuthenticationStateProvider(new FakePersonaStore { Stored = role });

        // bUnit pre-registers placeholders that throw unless its own test doubles
        // are used; the point here is to run the real pipeline, so they go.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationService>(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(services);

        Microsoft.Extensions.DependencyInjection.AuthorizationServiceCollectionExtensions.AddAuthorizationCore(
            services, options => WorkPlanStudio.Services.Auth.Permissions.AddWorkspacePolicies(options));
        Microsoft.Extensions.DependencyInjection.CascadingAuthenticationStateServiceCollectionExtensions.AddCascadingAuthenticationState(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, provider);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(services, provider);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<WorkPlanStudio.Services.Auth.IPermissionGuard, WorkPlanStudio.Services.Auth.PermissionGuard>(services);
        return provider;
    }
}
