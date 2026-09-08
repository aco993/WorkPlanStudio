using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services.Chat;

/// <summary>The on-device answer to a question, plus what was recognised.</summary>
/// <param name="Text">Localized answer.</param>
/// <param name="Intent">The recognised intent.</param>
/// <param name="WhatIfRule">For <see cref="ChatIntent.WhatIfRule"/>: the dispatch rule to try.</param>
public sealed record OfflineAnswer(string Text, ChatIntent Intent, DispatchRule? WhatIfRule = null);

/// <summary>
/// Answers questions about the schedule without a model: a small intent
/// recogniser over English and German keywords, and answers assembled from the
/// same context the page renders. Deterministic — the same question on the same
/// schedule always gets the same answer — which is what makes it the fallback
/// and the test oracle for the optional AI path.
/// </summary>
public sealed partial class OfflineScheduleAnswerer
{
    private readonly IStringLocalizer<SharedResource> _l;

    public OfflineScheduleAnswerer(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>Answers <paramref name="question"/> from <paramref name="context"/>.</summary>
    public OfflineAnswer Answer(ScheduleChatContext context, string question)
    {
        ArgumentNullException.ThrowIfNull(context);
        var q = (question ?? "").Trim();
        var lower = q.ToLowerInvariant();

        if (lower.Length == 0)
            return new OfflineAnswer(_l["Chat_Help"], ChatIntent.Help);

        // Specific things first: an order or a work center named in the question.
        var order = OrderReference().Match(q);
        if (order.Success && context.FindOrder(order.Value) is { } job)
            return new OfflineAnswer(DescribeOrder(context, job), ChatIntent.OrderStatus);

        var center = WorkCenterCode().Match(q);
        if (center.Success && context.FindWorkCenter(center.Value) is { } row)
            return new OfflineAnswer(DescribeWorkCenter(context, row), ChatIntent.WorkCenterStatus);

        if (RuleMentioned(lower) is { } rule)
            return new OfflineAnswer("", ChatIntent.WhatIfRule, rule);   // the façade runs it and phrases the comparison

        if (Help().IsMatch(lower))
            return new OfflineAnswer(_l["Chat_Help"], ChatIntent.Help);
        if (Bottleneck().IsMatch(lower))
            return new OfflineAnswer(DescribeBottleneck(context), ChatIntent.Bottleneck);
        if (Late().IsMatch(lower))
            return new OfflineAnswer(DescribeLate(context), ChatIntent.LateOrders);
        if (Compliance().IsMatch(lower))
            return new OfflineAnswer(DescribeCompliance(context), ChatIntent.Compliance);
        if (Closed().IsMatch(lower))
            return new OfflineAnswer(DescribeClosedTime(context), ChatIntent.ClosedTime);
        if (Summary().IsMatch(lower))
            return new OfflineAnswer(DescribeSummary(context), ChatIntent.Summary);

        return new OfflineAnswer(_l["Chat_Unknown"], ChatIntent.Unknown);
    }

    /// <summary>Phrases a what-if comparison after the façade has run the alternative rule.</summary>
    public string DescribeWhatIf(ScheduleChatContext current, DispatchRule rule, ScheduleResult alternative)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(alternative);
        var now = current.Schedule.Kpis;
        var alt = alternative.Kpis;

        if (rule == current.Parameters.DispatchRule)
            return _l["Chat_WhatIfSame", RuleName(rule)];

        string verdict = alt.LateJobCount < now.LateJobCount || (alt.LateJobCount == now.LateJobCount && alt.TotalTardinessSeconds < now.TotalTardinessSeconds)
            ? _l["Chat_WhatIfBetter"]
            : alt.LateJobCount == now.LateJobCount && alt.TotalTardinessSeconds == now.TotalTardinessSeconds && alt.MakespanSeconds == now.MakespanSeconds
                ? _l["Chat_WhatIfSameResult"]
                : _l["Chat_WhatIfWorse"];

        return _l["Chat_WhatIf", RuleName(rule), RuleName(current.Parameters.DispatchRule),
            now.LateJobCount, alt.LateJobCount, Hours(now.TotalTardinessSeconds), Hours(alt.TotalTardinessSeconds),
            Hours(now.MakespanSeconds), Hours(alt.MakespanSeconds), verdict];
    }

    // ----- answers -----

    private string DescribeSummary(ScheduleChatContext c)
    {
        var k = c.Schedule.Kpis;
        string text = _l["Chat_Summary", k.JobCount - k.LateJobCount, k.JobCount, Hours(k.MakespanSeconds), Hours(k.TotalTardinessSeconds), Percent(k.AverageUtilization)];
        if (c.Schedule.Explanation?.Bottleneck is { } b)
            text += " " + _l["Chat_SummaryBottleneck", b.WorkCenterName, Percent(b.Utilization)];
        return text;
    }

    private string DescribeBottleneck(ScheduleChatContext c)
    {
        if (c.Schedule.Explanation?.Bottleneck is not { } b)
            return _l["Chat_NoData"];
        var closed = c.FindWorkCenter(b.WorkCenterName.Split(' ')[0])?.Closed.Sum(x => x.DurationSeconds) ?? 0;
        return _l["Chat_Bottleneck", b.WorkCenterName, Percent(b.Utilization), b.OperationCount, Hours(closed)];
    }

    private string DescribeLate(ScheduleChatContext c)
    {
        var late = c.Schedule.Jobs.Where(j => j.IsLate).OrderByDescending(j => j.LatenessSeconds).ToList();
        if (late.Count == 0)
            return _l["Chat_NoneLate", c.Schedule.Kpis.JobCount];

        var lines = late.Take(5).Select(j =>
        {
            var finding = c.Schedule.Explanation?.LateJobs.FirstOrDefault(f => f.JobId == j.JobId);
            var reason = finding?.BlockingWorkCenterName is { } wc
                ? _l["Chat_LateReasonQueue", Hours(finding.QueueWaitSeconds), wc].Value
                : _l["Chat_LateReasonTight"].Value;
            return $"{j.Reference}: +{Hours(j.LatenessSeconds)} — {reason}";
        });
        string text = _l["Chat_Late", late.Count, c.Schedule.Kpis.JobCount] + "\n" + string.Join("\n", lines);
        if (c.Schedule.Explanation?.Recommendation is { Kind: RecommendationKind.SwitchDispatchRule } r)
            text += "\n" + _l["Chat_LateRecommend", RuleName(r.SuggestedRule!.Value), Hours(r.CurrentTardinessSeconds), Hours(r.ProjectedTardinessSeconds)];
        return text;
    }

    private string DescribeOrder(ScheduleChatContext c, JobRow job)
    {
        var horizon = c.Schedule.Horizon;
        var steps = c.Schedule.Rows
            .SelectMany(r => r.Bars.Where(b => b.JobId == job.JobId).Select(b => (Row: r, Bar: b)))
            .OrderBy(x => x.Bar.StepNumber)
            .ToList();
        var status = job.IsLate
            ? _l["Chat_OrderLate", Hours(job.LatenessSeconds)].Value
            : _l["Chat_OrderOnTime", Hours(-job.LatenessSeconds)].Value;
        string text = _l["Chat_Order", job.Reference, job.PartName, Stamp(job.DueSeconds, horizon), Stamp(job.CompletionSeconds, horizon), status];
        var stepLines = steps.Select(x =>
            $"  {x.Bar.StepNumber}. {x.Row.WorkCenterName}: {Stamp(x.Bar.StartSeconds, horizon)} – {Stamp(x.Bar.EndSeconds, horizon)}" +
            (x.Bar.PausedSeconds > 0 ? $" ({_l["Sched_Paused"]} {Hours(x.Bar.PausedSeconds)})" : ""));
        var finding = c.Schedule.Explanation?.LateJobs.FirstOrDefault(f => f.JobId == job.JobId);
        if (finding?.BlockingWorkCenterName is { } wc)
            text += " " + _l["Chat_LateReasonQueue", Hours(finding.QueueWaitSeconds), wc];
        return text + "\n" + string.Join("\n", stepLines);
    }

    private string DescribeWorkCenter(ScheduleChatContext c, GanttRow row)
    {
        var horizon = c.Schedule.Horizon;
        long busy = row.Bars.Sum(b => b.DurationSeconds - b.PausedSeconds);
        var util = c.Schedule.UtilizationByWorkCenter.TryGetValue(row.WorkCenterName, out var u) ? Percent(u) : "–";
        string text = _l["Chat_WorkCenter", row.WorkCenterName, row.Bars.Count, Hours(busy), util];
        var closedByKind = row.Closed.GroupBy(x => x.Kind).OrderByDescending(g => g.Sum(x => x.DurationSeconds))
            .Select(g => $"{_l[$"Segment_{g.Key}"]} {Hours(g.Sum(x => x.DurationSeconds))}");
        if (row.Closed.Count > 0)
            text += " " + _l["Chat_WorkCenterClosed", string.Join(", ", closedByKind)];
        var last = row.Bars.MaxBy(b => b.EndSeconds);
        if (last is not null)
            text += " " + _l["Chat_WorkCenterFree", Stamp(last.EndSeconds, horizon)];
        return text;
    }

    private string DescribeClosedTime(ScheduleChatContext c)
    {
        var all = c.Schedule.Rows.SelectMany(r => r.Closed).ToList();
        if (all.Count == 0)
            return _l["Chat_NoClosedTime"];
        var byKind = all.GroupBy(x => x.Kind).OrderByDescending(g => g.Sum(x => x.DurationSeconds))
            .Select(g => $"{_l[$"Segment_{g.Key}"]}: {Hours(g.Sum(x => x.DurationSeconds))}");
        string text = _l["Chat_ClosedTime", c.Schedule.Rows.Count] + "\n" + string.Join("\n", byKind);
        var holidays = c.HolidaysInHorizon.Select(h => $"{_l[$"Holiday_{h.Key}"]} ({h.Date.ToString("ddd d. MMM", CultureInfo.CurrentUICulture)})").ToList();
        if (holidays.Count > 0)
            text += "\n" + _l["Chat_ClosedHolidays", string.Join(", ", holidays)];
        return text;
    }

    private string DescribeCompliance(ScheduleChatContext c)
    {
        var r = c.Rules;
        var sunday = r.SundayWorkAllowed ? _l["Chat_Permitted"].Value : _l["Chat_Forbidden"].Value;
        var holiday = r.HolidayWorkAllowed ? _l["Chat_Permitted"].Value : _l["Chat_Forbidden"].Value;
        string text = _l["Chat_Compliance", r.State.Name(), r.State, sunday, holiday, r.DailyCap.TotalHours, r.MinimumRest.TotalHours,
            r.BreakAfterSixHours.TotalMinutes, r.BreakAfterNineHours.TotalMinutes];
        if (c.HolidaysInHorizon.Count > 0)
            text += " " + _l["Chat_ClosedHolidays", string.Join(", ", c.HolidaysInHorizon.Select(h => $"{_l[$"Holiday_{h.Key}"]} ({h.Date.ToString("ddd d. MMM", CultureInfo.CurrentUICulture)})"))];
        return text;
    }

    // ----- recognisers (English + German) -----

    [GeneratedRegex(@"\b(PO|WP)-\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderReference();

    [GeneratedRegex(@"\b[A-Z]{2,4}-\d{2,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex WorkCenterCode();

    [GeneratedRegex(@"\b(help|hilfe|what can you|was kannst|wobei|how do i ask)\b")]
    private static partial Regex Help();

    [GeneratedRegex(@"(bottleneck|engpass|busiest|constraint|ausgelastet|auslastung|utili[sz]ation|most loaded)")]
    private static partial Regex Bottleneck();

    [GeneratedRegex(@"(\blate\b|versp[äa]t|tardy|tardiness|delay|termin nicht|overdue|which orders miss)")]
    private static partial Regex Late();

    [GeneratedRegex(@"(arbzg|complian|comply|arbeitszeit|labou?r law|legal|rules?\b|regeln|sunday work|sonntagsarbeit|gesetz)")]
    private static partial Regex Compliance();

    [GeneratedRegex(@"(idle|closed|geschlossen|holiday|feiertag|sunday|sonntag|weekend|wochenende|break|pause|shift|schicht|why (is|does) .* (not|nothing)|warum .* nicht|stillstand|\bstill\b|\bstehen\b|absence|abwesen)")]
    private static partial Regex Closed();

    [GeneratedRegex(@"(summary|summari[sz]e|overview|zusammenfass|überblick|how (does|is) the schedule|wie sieht|status|makespan|on time|pünktlich|durchlaufzeit)")]
    private static partial Regex Summary();

    /// <summary>A dispatch rule named in the question, by acronym or by name in either language.</summary>
    internal static DispatchRule? RuleMentioned(string lower)
    {
        // Only a what-if phrasing counts; "EDD" alone in a status question is not a request to re-run.
        bool whatIf = Regex.IsMatch(lower, @"(what if|what about|try|switch|use|instead|compare|was w[äa]re|versuch|wechsel|stattdessen|nimm|mit der regel|rule|regel)");
        if (!whatIf)
            return null;

        if (Regex.IsMatch(lower, @"\bwspt\b|weighted")) return DispatchRule.WeightedShortestProcessingTime;
        if (Regex.IsMatch(lower, @"\bspt\b|shortest|kürzeste|kuerzeste")) return DispatchRule.ShortestProcessingTime;
        if (Regex.IsMatch(lower, @"\blpt\b|longest|längste|laengste")) return DispatchRule.LongestProcessingTime;
        if (Regex.IsMatch(lower, @"\bedd\b|earliest due|due date|liefertermin|frühester")) return DispatchRule.EarliestDueDate;
        if (Regex.IsMatch(lower, @"\bcr\b|critical ratio|kritisch")) return DispatchRule.CriticalRatio;
        if (Regex.IsMatch(lower, @"\bfifo\b|first in|first come|reihenfolge des eingangs")) return DispatchRule.Fifo;
        return null;
    }

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

    private static string Percent(double ratio) => (ratio * 100).ToString("0", CultureInfo.CurrentUICulture) + " %";

    private static string Stamp(long seconds, DateTime? horizon) =>
        horizon is { } h ? h.AddSeconds(seconds).ToString("ddd d.M. HH:mm", CultureInfo.CurrentUICulture) : Hours(seconds);
}
