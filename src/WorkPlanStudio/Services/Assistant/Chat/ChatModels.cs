using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Chat;

/// <summary>Who said a chat turn.</summary>
public enum ChatRole
{
    User,
    Assistant
}

/// <summary>One message in the schedule conversation.</summary>
/// <param name="Role">User or assistant.</param>
/// <param name="Text">The message, already localized for display.</param>
/// <param name="Source">For assistant turns: computed on-device or by a model.</param>
/// <param name="SourceLabel">Human label of the source ("On-device", "api.anthropic.com · claude-opus-5").</param>
/// <param name="Note">An optional caveat, e.g. that a model call fell back to the on-device answer.</param>
public sealed record ChatTurn(
    ChatRole Role,
    string Text,
    NarrationSource Source = NarrationSource.RuleBased,
    string SourceLabel = "",
    string? Note = null);

/// <summary>What the on-device answerer recognised in a question.</summary>
public enum ChatIntent
{
    /// <summary>Nothing recognised; the answer lists what can be asked.</summary>
    Unknown,

    /// <summary>
    /// An order or work-centre reference was recognised but does not exist in
    /// this schedule, or matches more than one row. Distinct from
    /// <see cref="Unknown"/>: the question *was* understood, and saying so is the
    /// difference between "I cannot find PO-9999" and a summary of a different
    /// schedule delivered as if it were the answer.
    /// </summary>
    UnknownReference,
    Summary,
    Bottleneck,
    LateOrders,
    OrderStatus,
    WorkCenterStatus,
    ClosedTime,
    WhatIfRule,
    Compliance,
    Help
}

/// <summary>Which model API the optional AI answers come from.</summary>
public enum AssistantProvider
{
    /// <summary>Any OpenAI-compatible <c>/chat/completions</c> endpoint: OpenAI, OpenRouter, Groq, Mistral, Ollama, LM Studio…</summary>
    OpenAiCompatible,

    /// <summary>Anthropic's Messages API (<c>/v1/messages</c>).</summary>
    Anthropic,

    /// <summary>Google's Gemini API (<c>generateContent</c>).</summary>
    Gemini
}

/// <summary>
/// Everything a question about the schedule can be answered from: the result
/// on the page, the parameters that produced it and the plant's working-time
/// rules. Built once per run; both the on-device answerer and the model prompt
/// read from it, so they can never disagree about the facts.
/// </summary>
public sealed record ScheduleChatContext(
    ScheduleResult Schedule,
    SchedulingParameters Parameters,
    WorkingTime.WorkingTimeRules Rules,
    IReadOnlyList<WorkingTime.PublicHoliday> HolidaysInHorizon)
{
    /// <summary>The separator <c>ScheduleMapper</c> puts between a work centre's code and its name.</summary>
    private const string LaneSeparator = " — ";

    /// <summary>
    /// Every order row matching <paramref name="reference"/> exactly, ignoring
    /// case. More than one is possible: the database's unique index on the order
    /// number is case-sensitive, so <c>PO-2000</c> and <c>po-2000</c> can both
    /// exist, and answering about "whichever sorts first" would be a wrong answer
    /// delivered confidently.
    /// </summary>
    public IReadOnlyList<JobRow> FindOrders(string reference) =>
        Schedule.Jobs.Where(j => string.Equals(j.Reference, reference, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The single order row for <paramref name="reference"/>, or null when there is none or more than one.</summary>
    public JobRow? FindOrder(string reference) => Single(FindOrders(reference));

    /// <summary>
    /// Every Gantt lane whose work-centre <b>code</b> is exactly
    /// <paramref name="code"/>. It used to be a prefix match, which meant a
    /// question about <c>CNC-3000</c> could be answered with <c>CNC-300</c>'s
    /// numbers — the worst shape of wrong, because the sentence names the right
    /// machine and the figure belongs to another one.
    /// </summary>
    public IReadOnlyList<GanttRow> FindWorkCenters(string code) =>
        Schedule.Rows.Where(r => string.Equals(CodeOf(r.WorkCenterName), code, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(r.WorkCenterName, code, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The single lane for <paramref name="code"/>, or null when there is none or more than one.</summary>
    public GanttRow? FindWorkCenter(string code) => Single(FindWorkCenters(code));

    /// <summary>The lane with exactly this display name, used to attach a figure to the lane it came from.</summary>
    public GanttRow? FindLane(string workCenterName) =>
        Schedule.Rows.FirstOrDefault(r => string.Equals(r.WorkCenterName, workCenterName, StringComparison.Ordinal));

    /// <summary>The code part of a lane label ("CNC-300 — Machining centre" → "CNC-300").</summary>
    internal static string CodeOf(string workCenterName)
    {
        int separator = workCenterName.IndexOf(LaneSeparator, StringComparison.Ordinal);
        return separator < 0 ? workCenterName : workCenterName[..separator];
    }

    private static T? Single<T>(IReadOnlyList<T> matches) where T : class => matches.Count == 1 ? matches[0] : null;
}
