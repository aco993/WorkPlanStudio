using WorkPlanStudio.Contracts;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services;

namespace WorkPlanStudio.Api.Mapping;

/// <summary>
/// Between the scheduling engine's own types and the wire.
/// <para>
/// The response carries the page's view model rather than raw engine output,
/// because the point of running the engine on a server is that the browser gets
/// the same picture it would have drawn itself — including the closed-time
/// shading and the computed explanation, which are part of the answer, not
/// decoration. <c>Signature</c> rides along so a caller can prove the two hosts
/// produced the same placement rather than merely a similar-looking one.
/// </para>
/// </summary>
public static class ScheduleMapping
{
    /// <summary>
    /// Turns a request into engine parameters, leaving anything the caller did
    /// not send at the engine's default.
    /// </summary>
    /// <param name="request">The posted parameters; null is treated as "all defaults".</param>
    /// <returns>Parameters, not yet range-checked — <c>SchedulingParameterLimits.Validate</c> does that.</returns>
    public static SchedulingParameters ToParameters(ScheduleRunRequest? request)
    {
        var defaults = new SchedulingParameters();
        if (request is null)
            return defaults;

        return defaults with
        {
            DispatchRule = (DispatchRule?)request.DispatchRule ?? defaults.DispatchRule,
            DueDateRule = (DueDateRule?)request.DueDateRule ?? defaults.DueDateRule,
            TwkFlowFactor = request.TwkFlowFactor ?? defaults.TwkFlowFactor,
            NopSecondsPerOp = request.NopSecondsPerOp ?? defaults.NopSecondsPerOp,
            SlackSeconds = request.SlackSeconds ?? defaults.SlackSeconds,
            ConstantAllowanceSeconds = request.ConstantAllowanceSeconds ?? defaults.ConstantAllowanceSeconds,
            MultiStartRuns = request.MultiStartRuns ?? defaults.MultiStartRuns,
            LocalSearchMaxSteps = request.LocalSearchMaxSteps ?? defaults.LocalSearchMaxSteps,
            Seed = request.Seed ?? defaults.Seed,
            MakespanWeight = request.MakespanWeight ?? defaults.MakespanWeight,
            TardinessWeight = request.TardinessWeight ?? defaults.TardinessWeight,
            LatePenalty = request.LatePenalty ?? defaults.LatePenalty
        };
    }

    /// <summary>
    /// The Gantt's display day for this request. It is a rendering choice, not an
    /// engine parameter, so it travels beside the parameters rather than inside
    /// them — the same split the browser makes.
    /// </summary>
    /// <param name="request">The run request.</param>
    public static int MinutesPerWorkingDay(ScheduleRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MinutesPerWorkingDay ?? ScheduleResult.DefaultMinutesPerWorkingDay;
    }

    /// <summary>Projects a completed run for the wire.</summary>
    /// <param name="result">The view model the page would render.</param>
    /// <param name="signature">The engine's canonical fingerprint, or an empty string when nothing was scheduled.</param>
    public static ScheduleRunResponse ToResponse(ScheduleResult result, string signature)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ScheduleRunResponse(
            result.HasData,
            new ScheduleKpisDto(
                result.Kpis.MakespanSeconds,
                result.Kpis.OnTimeRate,
                result.Kpis.TotalTardinessSeconds,
                result.Kpis.AverageUtilization,
                result.Kpis.LateJobCount,
                result.Kpis.JobCount),
            [.. result.Rows.Select(row => new GanttRowDto(
                row.WorkCenterName,
                [.. row.Bars.Select(bar => new GanttBarDto(
                    bar.JobId, bar.JobReference, bar.ColorIndex, bar.StepNumber,
                    bar.StartSeconds, bar.EndSeconds, bar.IsLate, bar.PausedSeconds, bar.SetupSeconds))],
                [.. row.Closed.Select(segment => new GanttClosedSegmentDto(
                    segment.StartSeconds, segment.EndSeconds, (int)segment.Kind, segment.Label))]))],
            [.. result.Jobs.Select(job => new JobRowDto(
                job.JobId, job.Reference, job.PartName, job.ColorIndex,
                job.DueSeconds, job.CompletionSeconds, job.LatenessSeconds, job.IsLate))],
            result.MakespanSeconds,
            result.MinutesPerWorkingDay,
            result.LocalSearchSteps,
            result.Horizon,
            result.TotalPausedSeconds,
            result.UtilizationByWorkCenter.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            [.. result.EquivalentRules.Select(rule => (int)rule)],
            [.. result.PreparationErrors.Select(issue => new SchedulePreparationIssueDto(
                issue.OrderId, issue.OrderReference, issue.OperationNumber, (int)issue.Code, issue.WorkCenterReference))],
            ToDto(result.Explanation),
            signature);
    }

    private static ScheduleExplanationDto? ToDto(ScheduleExplanation? explanation) =>
        explanation is null
            ? null
            : new ScheduleExplanationDto(
                explanation.Summary.JobCount,
                explanation.Summary.OnTimeCount,
                explanation.Summary.MakespanSeconds,
                explanation.Summary.TotalTardinessSeconds,
                explanation.Summary.AverageUtilization,
                explanation.Bottleneck?.WorkCenterId,
                explanation.Bottleneck?.WorkCenterName,
                explanation.Bottleneck?.Utilization ?? 0,
                explanation.Bottleneck?.OperationCount ?? 0,
                [.. explanation.LateJobs.Select(job => new LateJobFindingDto(
                    job.JobId, job.JobReference, job.TardinessSeconds, job.QueueWaitSeconds, job.BlockingWorkCenterName))],
                (int)explanation.Recommendation.Kind,
                (int)explanation.Recommendation.CurrentRule,
                (int?)explanation.Recommendation.SuggestedRule,
                explanation.Recommendation.CurrentTardinessSeconds,
                explanation.Recommendation.ProjectedTardinessSeconds);
}
