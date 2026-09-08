using System.Globalization;
using System.Text;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services.Chat;

/// <summary>
/// Renders a <see cref="ScheduleChatContext"/> as compact, invariant-culture
/// facts for a model — the only thing a model is ever told about the schedule.
/// Deterministic and unit-testable; numbers here are the numbers on the page.
/// </summary>
public static class ChatFacts
{
    /// <summary>The standing instruction for a model answering questions about a schedule.</summary>
    public const string System =
        "You are the scheduling assistant inside WorkPlan Studio, a production-planning tool. " +
        "Answer the planner's questions about the current schedule using only the facts below. " +
        "Be concise and concrete: name orders, work centers, hours and dates from the facts. " +
        "If a question cannot be answered from the facts, say so and suggest what the planner could check. " +
        "Never invent numbers.";

    /// <summary>Everything a model may know about the current run.</summary>
    public static string Describe(ScheduleChatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var s = context.Schedule;
        var sb = new StringBuilder();
        var horizon = s.Horizon;

        sb.AppendLine("## Schedule");
        sb.AppendLine($"- Dispatch rule: {context.Parameters.DispatchRule}; target-date rule: {context.Parameters.DueDateRule}; seed {context.Parameters.Seed}.");
        if (horizon is { } h)
            sb.AppendLine($"- Horizon (second 0): {h:yyyy-MM-dd HH:mm}. All times below are plant-local.");
        sb.AppendLine($"- Orders: {s.Kpis.JobCount}; on time: {s.Kpis.JobCount - s.Kpis.LateJobCount}; late: {s.Kpis.LateJobCount}; " +
                      $"makespan {Hours(s.Kpis.MakespanSeconds)}; total tardiness {Hours(s.Kpis.TotalTardinessSeconds)}; " +
                      $"average utilisation {Percent(s.Kpis.AverageUtilization)}; paused across breaks {Hours(s.TotalPausedSeconds)}.");

        sb.AppendLine("## Orders");
        foreach (var job in s.Jobs)
        {
            sb.AppendLine($"- {job.Reference} ({job.PartName}): target {Stamp(job.DueSeconds, horizon)}, completion {Stamp(job.CompletionSeconds, horizon)}, " +
                          (job.IsLate ? $"LATE by {Hours(job.LatenessSeconds)}" : $"early by {Hours(-job.LatenessSeconds)}") + ".");
        }

        sb.AppendLine("## Work centers");
        foreach (var row in s.Rows)
        {
            long busy = row.Bars.Sum(b => b.DurationSeconds - b.PausedSeconds);
            var closed = row.Closed.GroupBy(c => c.Kind).Select(g => $"{g.Key} {Hours(g.Sum(c => c.DurationSeconds))}");
            var util = s.UtilizationByWorkCenter.TryGetValue(row.WorkCenterName, out var u) ? Percent(u) : "n/a";
            sb.AppendLine($"- {row.WorkCenterName}: {row.Bars.Count} operations, busy {Hours(busy)}, utilisation {util}" +
                          (row.Closed.Count > 0 ? $"; closed: {string.Join(", ", closed)}" : "") + ".");
            foreach (var bar in row.Bars.OrderBy(b => b.StartSeconds))
                sb.AppendLine($"  - {bar.JobReference} op {bar.StepNumber}: {Stamp(bar.StartSeconds, horizon)} – {Stamp(bar.EndSeconds, horizon)}" +
                              (bar.PausedSeconds > 0 ? $" (paused {Hours(bar.PausedSeconds)})" : "") + ".");
        }

        if (s.Explanation is { } e)
        {
            sb.AppendLine("## Analysis");
            if (e.Bottleneck is { } b)
                sb.AppendLine($"- Bottleneck: {b.WorkCenterName} at {Percent(b.Utilization)} over {b.OperationCount} operations.");
            foreach (var late in e.LateJobs)
                sb.AppendLine($"- {late.JobReference} is late by {Hours(late.TardinessSeconds)}; waited {Hours(late.QueueWaitSeconds)}" +
                              (late.BlockingWorkCenterName is { } wc ? $", mostly for {wc}" : ", its target is tighter than its processing time") + ".");
            sb.AppendLine("- Recommendation: " + e.Recommendation.Kind switch
            {
                RecommendationKind.AlreadyOnTime => "every order meets its target.",
                RecommendationKind.SwitchDispatchRule => $"switching to {e.Recommendation.SuggestedRule} is estimated to cut tardiness from {Hours(e.Recommendation.CurrentTardinessSeconds)} to {Hours(e.Recommendation.ProjectedTardinessSeconds)}.",
                _ => "no other dispatch rule did better; the lateness is structural."
            });
        }

        sb.AppendLine("## Working-time rules (Arbeitszeitgesetz)");
        var r = context.Rules;
        sb.AppendLine($"- State: {r.State} ({r.State.Name()}); Sunday work {(r.SundayWorkAllowed ? "permitted (§10)" : "forbidden (§9)")}; holiday work {(r.HolidayWorkAllowed ? "permitted" : "forbidden")}; " +
                      $"daily cap {r.DailyCap.TotalHours:0} h; rest {r.MinimumRest.TotalHours:0} h; breaks {r.BreakAfterSixHours.TotalMinutes:0}/{r.BreakAfterNineHours.TotalMinutes:0} min.");
        if (context.HolidaysInHorizon.Count > 0)
            sb.AppendLine("- Public holidays inside the schedule: " + string.Join(", ", context.HolidaysInHorizon.Select(h => $"{h.NameEn} ({h.Date:yyyy-MM-dd})")) + ".");

        return sb.ToString();
    }

    internal static string Hours(long seconds) => (seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + " h";

    internal static string Percent(double ratio) => (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    internal static string Stamp(long seconds, DateTime? horizon) =>
        horizon is { } h ? h.AddSeconds(seconds).ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : Hours(seconds);
}
