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
    /// <summary>The order rows by reference, case-insensitive.</summary>
    public JobRow? FindOrder(string reference) =>
        Schedule.Jobs.FirstOrDefault(j => string.Equals(j.Reference, reference, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Gantt lane whose work-center name starts with <paramref name="code"/> (e.g. "CNC-300").</summary>
    public GanttRow? FindWorkCenter(string code) =>
        Schedule.Rows.FirstOrDefault(r => r.WorkCenterName.StartsWith(code, StringComparison.OrdinalIgnoreCase));
}
