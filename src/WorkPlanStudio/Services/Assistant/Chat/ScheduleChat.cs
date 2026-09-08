using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Chat;

/// <summary>
/// The conversation over the current schedule. Every question is first
/// answered on-device by <see cref="OfflineScheduleAnswerer"/>; when the user
/// has configured a model, the same facts plus the conversation go to the
/// provider and its answer is shown instead — with the on-device answer as the
/// fallback on any failure, so the chat always answers. A what-if question
/// actually re-runs the scheduler with the alternative rule and compares.
/// </summary>
public sealed class ScheduleChat
{
    /// <summary>How many recent turns a model is shown; older ones fall off to bound the prompt.</summary>
    public const int HistoryWindow = 12;

    private readonly OfflineScheduleAnswerer _offline;
    private readonly IAssistantConfig _config;
    private readonly IProductionScheduleService _scheduler;
    private readonly HttpClient _http;
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly List<ChatTurn> _turns = [];

    public ScheduleChat(
        OfflineScheduleAnswerer offline,
        IAssistantConfig config,
        IProductionScheduleService scheduler,
        HttpClient http,
        IStringLocalizer<SharedResource> l)
    {
        _offline = offline;
        _config = config;
        _scheduler = scheduler;
        _http = http;
        _l = l;
    }

    /// <summary>The facts the conversation is about; set by the page after every run.</summary>
    public ScheduleChatContext? Context { get; private set; }

    /// <summary>The conversation so far, oldest first.</summary>
    public IReadOnlyList<ChatTurn> Turns => _turns;

    /// <summary>Points the conversation at a new run and clears it — an answer about an old schedule would mislead.</summary>
    public void Reset(ScheduleChatContext? context)
    {
        Context = context;
        _turns.Clear();
    }

    /// <summary>Questions a first-time user can click instead of typing.</summary>
    public IReadOnlyList<string> Suggestions =>
    [
        _l["Chat_Suggest_Bottleneck"], _l["Chat_Suggest_Late"], _l["Chat_Suggest_Closed"],
        _l["Chat_Suggest_WhatIf"], _l["Chat_Suggest_Rules"]
    ];

    /// <summary>Answers a question and appends both turns to the conversation.</summary>
    public async Task<ChatTurn> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        var text = (question ?? "").Trim();
        if (text.Length == 0)
            throw new ArgumentException("A question is required.", nameof(question));
        if (Context is null)
            return Append(new ChatTurn(ChatRole.Assistant, _l["Chat_NoData"], NarrationSource.RuleBased, _l["Chat_SourceOffline"]));

        _turns.Add(new ChatTurn(ChatRole.User, text));

        var offline = _offline.Answer(Context, text);
        string offlineText = offline.Text;
        string? computedFacts = null;

        if (offline.Intent == ChatIntent.WhatIfRule && offline.WhatIfRule is { } rule)
        {
            var alternative = await _scheduler.GenerateAsync(Context.Parameters with { DispatchRule = rule }, cancellationToken);
            offlineText = _offline.DescribeWhatIf(Context, rule, alternative);
            computedFacts = $"What-if computed by the scheduler for dispatch rule {rule}: " +
                            $"late {alternative.Kpis.LateJobCount} (now {Context.Schedule.Kpis.LateJobCount}), " +
                            $"tardiness {ChatFacts.Hours(alternative.Kpis.TotalTardinessSeconds)} (now {ChatFacts.Hours(Context.Schedule.Kpis.TotalTardinessSeconds)}), " +
                            $"makespan {ChatFacts.Hours(alternative.Kpis.MakespanSeconds)} (now {ChatFacts.Hours(Context.Schedule.Kpis.MakespanSeconds)}).";
        }

        var settings = await _config.LoadAsync();
        if (!settings.IsConfigured)
            return Append(new ChatTurn(ChatRole.Assistant, offlineText, NarrationSource.RuleBased, _l["Chat_SourceOffline"]));

        try
        {
            var provider = ChatProviders.Create(_http, settings);
            var system = ChatFacts.System +
                         $" Respond in the language with ISO code '{CultureInfo.CurrentUICulture.TwoLetterISOLanguageName}'.\n\n" +
                         ChatFacts.Describe(Context) +
                         (computedFacts is null ? "" : "\n## Computed for this question\n- " + computedFacts) +
                         "\n## On-device answer to the last question (facts you may rephrase)\n" + offlineText;
            var history = _turns.TakeLast(HistoryWindow).ToList();
            var answer = await provider.CompleteAsync(system, history, cancellationToken);
            return Append(new ChatTurn(ChatRole.Assistant, answer, NarrationSource.Ai, provider.Label));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _turns.RemoveAt(_turns.Count - 1);
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or OperationCanceledException or JsonException
               or InvalidOperationException or NotSupportedException or UriFormatException or ArgumentException)
        {
            return Append(new ChatTurn(ChatRole.Assistant, offlineText, NarrationSource.RuleBased, _l["Chat_SourceOffline"],
                Note: _l["Sched_Ai_Fallback", FailureLabel(ex)]));
        }
    }

    private ChatTurn Append(ChatTurn turn)
    {
        _turns.Add(turn);
        return turn;
    }

    private string FailureLabel(Exception exception) => exception switch
    {
        OperationCanceledException => _l["Sched_Ai_FailureTimeout"],
        JsonException or InvalidOperationException or NotSupportedException => _l["Sched_Ai_FailureResponse"],
        _ => _l["Sched_Ai_FailureUnavailable"]
    };
}
