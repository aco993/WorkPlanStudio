using System.Globalization;
using System.Text;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Builds the prompt for the AI narrator from a structured explanation. Kept pure
/// and culture-invariant so the facts are deterministic and directly unit-testable —
/// the model only ever rephrases numbers it was given, it never invents them.
/// </summary>
internal static class AssistantPrompt
{
    /// <summary>The fixed system instruction.</summary>
    public const string System =
        "You are a production-scheduling assistant. Given the facts about a finite-capacity " +
        "schedule, explain the result to a planner in clear, concise language: what happened, " +
        "where the constraint is, why any jobs are late, and what to try next. Use short bullet " +
        "points and do not invent any numbers beyond the facts provided.";

    /// <summary>Renders the explanation as a compact, factual bullet list for the model.</summary>
    public static string BuildFacts(ScheduleExplanation e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var sb = new StringBuilder();
        var s = e.Summary;
        sb.AppendLine("Schedule facts:");
        sb.AppendLine($"- Jobs: {s.JobCount}; on time: {s.OnTimeCount}; late: {s.JobCount - s.OnTimeCount}.");
        sb.AppendLine($"- Makespan: {Hours(s.MakespanSeconds)}; total tardiness: {Hours(s.TotalTardinessSeconds)}; average utilisation: {Percent(s.AverageUtilization)}.");

        if (e.Bottleneck is { } b)
            sb.AppendLine($"- Busiest work center: {b.WorkCenterName} at {Percent(b.Utilization)} utilisation across {b.OperationCount} operations.");

        foreach (var j in e.LateJobs)
        {
            sb.AppendLine(j.BlockingWorkCenterName is { } wc
                ? $"- Late job {j.JobReference}: {Hours(j.TardinessSeconds)} late, spent {Hours(j.QueueWaitSeconds)} waiting, mostly for {wc}."
                : $"- Late job {j.JobReference}: {Hours(j.TardinessSeconds)} late; its target is tighter than its own processing time.");
        }

        var r = e.Recommendation;
        sb.Append("- Recommendation basis: ");
        sb.AppendLine(r.Kind switch
        {
            RecommendationKind.AlreadyOnTime => "every job meets its target.",
            RecommendationKind.SwitchDispatchRule =>
                $"switching the dispatch rule from {r.CurrentRule} to {r.SuggestedRule} is estimated to cut total tardiness from {Hours(r.CurrentTardinessSeconds)} to {Hours(r.ProjectedTardinessSeconds)}.",
            _ => "no alternative dispatch rule improved on the current one; the lateness looks structural."
        });

        return sb.ToString();
    }

    private static string Hours(long seconds) =>
        (seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + " h";

    private static string Percent(double ratio) =>
        (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
}
