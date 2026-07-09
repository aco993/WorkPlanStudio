using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// The default, deterministic narrator: turns a structured explanation into localized
/// lines with no network and no key. It doubles as the fallback when an AI call fails
/// and as the "mock" provider used by the tests and the public demo.
/// </summary>
public sealed class RuleBasedNarrator : IScheduleNarrator
{
    private readonly IStringLocalizer<SharedResource> _l;

    public RuleBasedNarrator(IStringLocalizer<SharedResource> l) => _l = l;

    /// <inheritdoc />
    public string SourceLabel => _l["Sched_Ai_SourceRuleBased"];

    /// <inheritdoc />
    public Task<NarrationResult> NarrateAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(explanation);
        return Task.FromResult(new NarrationResult(BuildLines(explanation), NarrationSource.RuleBased, SourceLabel));
    }

    /// <summary>Composes the localized narration lines. Exposed for direct unit testing.</summary>
    public IReadOnlyList<NarrationLine> BuildLines(ScheduleExplanation e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var lines = new List<NarrationLine>();
        var s = e.Summary;

        lines.Add(new NarrationLine(
            _l["Sched_Ai_Summary", s.OnTimeCount, s.JobCount, Hours(s.MakespanSeconds), Percent(s.AverageUtilization)],
            s.TotalTardinessSeconds == 0 ? FindingTone.Good : FindingTone.Warning));

        if (e.Bottleneck is { } b)
        {
            lines.Add(new NarrationLine(
                _l["Sched_Ai_Bottleneck", b.WorkCenterName, Percent(b.Utilization), b.OperationCount],
                b.Utilization >= 0.85 ? FindingTone.Warning : FindingTone.Info));
        }

        foreach (var j in e.LateJobs)
        {
            lines.Add(new NarrationLine(
                j.BlockingWorkCenterName is { } wc
                    ? _l["Sched_Ai_LateBlocked", j.JobReference, Hours(j.TardinessSeconds), wc]
                    : _l["Sched_Ai_LateTight", j.JobReference, Hours(j.TardinessSeconds)],
                FindingTone.Warning));
        }

        int hiddenLate = (s.JobCount - s.OnTimeCount) - e.LateJobs.Count;
        if (hiddenLate > 0)
            lines.Add(new NarrationLine(_l["Sched_Ai_MoreLate", hiddenLate], FindingTone.Info));

        lines.Add(RecommendationLine(e.Recommendation));
        return lines;
    }

    private NarrationLine RecommendationLine(ScheduleRecommendation r) => r.Kind switch
    {
        RecommendationKind.AlreadyOnTime =>
            new NarrationLine(_l["Sched_Ai_RecOnTime"], FindingTone.Good),
        RecommendationKind.SwitchDispatchRule =>
            new NarrationLine(
                _l["Sched_Ai_RecSwitch", RuleName(r.SuggestedRule!.Value), Hours(r.CurrentTardinessSeconds), Hours(r.ProjectedTardinessSeconds)],
                FindingTone.Info),
        _ =>
            new NarrationLine(_l["Sched_Ai_RecNone"], FindingTone.Warning)
    };

    private string RuleName(DispatchRule rule) => _l[rule switch
    {
        DispatchRule.Fifo => "Sched_Rule_Fifo",
        DispatchRule.ShortestProcessingTime => "Sched_Rule_Spt",
        DispatchRule.LongestProcessingTime => "Sched_Rule_Lpt",
        DispatchRule.EarliestDueDate => "Sched_Rule_Edd",
        DispatchRule.CriticalRatio => "Sched_Rule_Cr",
        DispatchRule.WeightedShortestProcessingTime => "Sched_Rule_Wspt",
        _ => "Sched_Rule_Fifo"
    }];

    private static string Hours(long seconds) => Format.Hours(seconds / 60m);

    private static string Percent(double ratio) => (ratio * 100).ToString("0") + " %";
}
