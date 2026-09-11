using WorkPlanStudio.Contracts;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services.Scheduling;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services.Remote;

/// <summary>
/// Runs the schedule on the server instead of in this browser.
/// <para>
/// It implements the same <see cref="IProductionScheduleService"/> the page
/// already depends on, so the scheduling page does not know or care which side
/// of the wire the engine ran on. That is only honest because it is literally
/// the same engine over the same mapper — the API compiles this repository's
/// own <c>ScheduleMapper</c> — and the API test suite asserts that both hosts
/// produce the identical placement signature for the same input.
/// </para>
/// </summary>
public sealed class RemoteScheduleService : IProductionScheduleService
{
    private readonly ApiClient _api;

    /// <summary>Creates the service.</summary>
    /// <param name="api">The authenticated API client.</param>
    public RemoteScheduleService(ApiClient api) => _api = api;

    /// <inheritdoc />
    public Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(parameters, ScheduleResult.DefaultMinutesPerWorkingDay, progress: null, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A server run reports no intermediate progress: the search happens in one
    /// request and there is no channel back until it answers. The caller is told
    /// the run started and then told it finished, which is the truth — inventing
    /// restart counts for a progress bar would be a lie the UI could not detect.
    /// </remarks>
    public async Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        int minutesPerWorkingDay,
        IProgress<ScheduleRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        SchedulingParameterLimits.Validate(parameters);

        progress?.Report(new ScheduleRunProgress(0, parameters.MultiStartRuns, double.PositiveInfinity));
        var response = await _api.RunScheduleAsync(
            RemoteMapping.ToRequest(parameters, minutesPerWorkingDay), cancellationToken);
        var result = RemoteMapping.ToResult(response);
        progress?.Report(new ScheduleRunProgress(
            parameters.MultiStartRuns, parameters.MultiStartRuns, double.PositiveInfinity));
        return result;
    }
}

/// <summary>
/// Wire shapes back into the app's own view models.
/// <para>
/// Written by hand and in one place, the mirror image of the API's
/// <c>ScheduleMapping</c>. Enums cross as integers, so a value added on one
/// side and not the other shows up as an unknown number rather than as a
/// silently different meaning.
/// </para>
/// </summary>
public static class RemoteMapping
{
    /// <summary>Flattens the engine parameters for the wire.</summary>
    /// <param name="parameters">The parameters the page produced.</param>
    /// <param name="minutesPerWorkingDay">The Gantt's display day, a rendering choice that travels beside the parameters.</param>
    public static ScheduleRunRequest ToRequest(SchedulingParameters parameters, int minutesPerWorkingDay)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return new ScheduleRunRequest
        {
            DispatchRule = (int)parameters.DispatchRule,
            DueDateRule = (int)parameters.DueDateRule,
            TwkFlowFactor = parameters.TwkFlowFactor,
            NopSecondsPerOp = parameters.NopSecondsPerOp,
            SlackSeconds = parameters.SlackSeconds,
            ConstantAllowanceSeconds = parameters.ConstantAllowanceSeconds,
            MultiStartRuns = parameters.MultiStartRuns,
            LocalSearchMaxSteps = parameters.LocalSearchMaxSteps,
            Seed = parameters.Seed,
            MakespanWeight = parameters.MakespanWeight,
            TardinessWeight = parameters.TardinessWeight,
            LatePenalty = parameters.LatePenalty,
            MinutesPerWorkingDay = minutesPerWorkingDay
        };
    }

    /// <summary>Rebuilds the page's view model from a server run.</summary>
    /// <param name="response">What the schedule endpoint returned.</param>
    public static ScheduleResult ToResult(ScheduleRunResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var kpis = new ScheduleKpis(
            response.Kpis.MakespanSeconds,
            response.Kpis.OnTimeRate,
            response.Kpis.TotalTardinessSeconds,
            response.Kpis.AverageUtilization,
            response.Kpis.LateJobCount,
            response.Kpis.JobCount);

        var rows = response.Rows
            .Select(row => new GanttRow(
                row.WorkCenterName,
                [.. row.Bars.Select(bar => new GanttBar(
                    bar.JobId, bar.JobReference, bar.ColorIndex, bar.StepNumber,
                    bar.StartSeconds, bar.EndSeconds, bar.IsLate)
                {
                    PausedSeconds = bar.PausedSeconds,
                    SetupSeconds = bar.SetupSeconds
                })],
                [.. row.Closed.Select(segment => new GanttClosedSegment(
                    segment.StartSeconds, segment.EndSeconds, (SegmentKind)segment.Kind, segment.Label))]))
            .ToList();

        var jobs = response.Jobs
            .Select(job => new JobRow(
                job.JobId, job.Reference, job.PartName, job.ColorIndex,
                job.DueSeconds, job.CompletionSeconds, job.LatenessSeconds, job.IsLate))
            .ToList();

        return new ScheduleResult(
            response.HasData, kpis, rows, jobs,
            response.MakespanSeconds, response.MinutesPerWorkingDay, response.LocalSearchSteps)
        {
            Horizon = response.HorizonUtc,
            TotalPausedSeconds = response.TotalPausedSeconds,
            UtilizationByWorkCenter = response.UtilizationByWorkCenter.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            EquivalentRules = [.. response.EquivalentRules.Select(rule => (DispatchRule)rule)],
            PreparationErrors = [.. response.PreparationErrors.Select(issue => new SchedulePreparationIssue(
                issue.OrderId,
                issue.OrderReference,
                issue.OperationNumber,
                (SchedulePreparationErrorCode)issue.Code,
                issue.WorkCenterReference))],
            Explanation = ToExplanation(response.Explanation)
        };
    }

    private static ScheduleExplanation? ToExplanation(ScheduleExplanationDto? dto) =>
        dto is null
            ? null
            : new ScheduleExplanation(
                new ScheduleSummary(
                    dto.JobCount, dto.OnTimeCount, dto.MakespanSeconds, dto.TotalTardinessSeconds, dto.AverageUtilization),
                dto.BottleneckWorkCenterId is { } workCenterId
                    ? new BottleneckFinding(
                        workCenterId, dto.BottleneckWorkCenterName ?? "", dto.BottleneckUtilization, dto.BottleneckOperationCount)
                    : null,
                [.. dto.LateJobs.Select(job => new LateJobFinding(
                    job.JobId, job.JobReference, job.TardinessSeconds, job.QueueWaitSeconds, job.BlockingWorkCenterName))],
                new ScheduleRecommendation(
                    (RecommendationKind)dto.RecommendationKind,
                    (DispatchRule)dto.CurrentRule,
                    dto.SuggestedRule is { } suggested ? (DispatchRule)suggested : null,
                    dto.CurrentTardinessSeconds,
                    dto.ProjectedTardinessSeconds));
}
