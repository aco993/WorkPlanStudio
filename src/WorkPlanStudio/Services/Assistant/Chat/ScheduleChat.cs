using System.Globalization;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Chat;

/// <summary>What the conversation is doing; the page's spinner and its disabled buttons read this.</summary>
public enum ChatActivity
{
    /// <summary>No question in flight.</summary>
    Idle,

    /// <summary>Answering on-device, or re-running the scheduler for a what-if.</summary>
    Answering,

    /// <summary>Waiting for the configured model.</summary>
    AskingModel
}

/// <summary>
/// The conversation over the current schedule. Every question is first
/// answered on-device by <see cref="OfflineScheduleAnswerer"/>; when the user
/// has configured a model, the same facts plus the conversation go to the
/// provider and its answer is shown instead — with the on-device answer as the
/// fallback on any failure, so the chat always answers. A what-if question
/// actually re-runs the scheduler with the alternative rule and compares.
/// <para>
/// The class owns mutable conversation state across awaits, so it is explicit
/// about concurrency: one question at a time (a semaphore), one conversation
/// generation (a counter), and an in-flight request that is cancelled the
/// moment the conversation is pointed at a different schedule. Without that, an
/// answer computed against the previous run could be appended, labelled and read
/// as an answer about the current one.
/// </para>
/// </summary>
public sealed class ScheduleChat : IDisposable
{
    /// <summary>How many recent turns a model is shown; older ones fall off to bound the prompt.</summary>
    public const int HistoryWindow = 12;

    /// <summary>
    /// Longest question accepted. A pasted megabyte would otherwise be sent, and
    /// billed, verbatim; the cap is far above any real question.
    /// </summary>
    public const int MaxQuestionCharacters = 1000;

    private readonly OfflineScheduleAnswerer _offline;
    private readonly IAssistantConfig _config;
    private readonly IProductionScheduleService _scheduler;
    private readonly HttpClient _http;
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly List<ChatTurn> _turns = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource _conversation = new();
    private int _generation;
    private bool _disposed;

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

    /// <summary>Where the conversation is in its cycle. The page should disable Generate and Clear while this is not <see cref="ChatActivity.Idle"/>.</summary>
    public ChatActivity Activity { get; private set; } = ChatActivity.Idle;

    /// <summary>True while a question is being answered.</summary>
    public bool IsBusy => Activity != ChatActivity.Idle;

    /// <summary>
    /// Points the conversation at a new run and clears it — an answer about an
    /// old schedule would mislead. A question still in flight is cancelled here
    /// rather than allowed to land in the cleared thread.
    /// </summary>
    public void Reset(ScheduleChatContext? context)
    {
        if (_disposed)
            return;   // the page is going away; resetting a conversation nobody will read is not an error

        _generation++;
        var superseded = _conversation;
        _conversation = new CancellationTokenSource();
        superseded.Cancel();
        superseded.Dispose();

        Context = context;
        _turns.Clear();
    }

    /// <summary>Questions a first-time user can click instead of typing.</summary>
    public IReadOnlyList<string> Suggestions =>
    [
        _l["Chat_Suggest_Bottleneck"], _l["Chat_Suggest_Late"], _l["Chat_Suggest_Closed"],
        _l["Chat_Suggest_WhatIf"], _l["Chat_Suggest_Rules"]
    ];

    /// <summary>
    /// Answers a question and appends both turns to the conversation. Two
    /// questions never interleave: the second waits for the first.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The caller cancelled, or <see cref="Reset"/> pointed the conversation at a
    /// different schedule while this question was in flight. In both cases the
    /// question is removed again and nothing is appended.
    /// </exception>
    public async Task<ChatTurn> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        var text = (question ?? "").Trim();
        if (text.Length == 0)
            throw new ArgumentException("A question is required.", nameof(question));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await AnswerAsync(text, cancellationToken);
        }
        finally
        {
            Activity = ChatActivity.Idle;
            _gate.Release();
        }
    }

    private async Task<ChatTurn> AnswerAsync(string text, CancellationToken cancellationToken)
    {
        if (Context is not { } context)
            return Append(new ChatTurn(ChatRole.Assistant, _l["Chat_NoData"], NarrationSource.RuleBased, _l["Chat_SourceOffline"]));

        int generation = _generation;
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _conversation.Token);
        var token = scope.Token;

        bool tooLong = text.Length > MaxQuestionCharacters;
        var asked = new ChatTurn(ChatRole.User, tooLong ? text[..MaxQuestionCharacters] + "…" : text);
        _turns.Add(asked);

        if (tooLong)
            return Append(new ChatTurn(ChatRole.Assistant, _l["Ai_QuestionTooLong", MaxQuestionCharacters],
                NarrationSource.RuleBased, _l["Chat_SourceOffline"]));

        Activity = ChatActivity.Answering;
        var offline = _offline.Answer(context, text);
        string offlineText = offline.Text;
        string? computedFacts = null;

        try
        {
            if (offline.Intent == ChatIntent.WhatIfRule && offline.WhatIfRule is { } rule)
            {
                var alternative = await _scheduler.GenerateAsync(context.Parameters with { DispatchRule = rule }, token);
                offlineText = _offline.DescribeWhatIf(context, rule, alternative);
                computedFacts = $"What-if computed by the scheduler for dispatch rule {rule}: " +
                                $"late {alternative.Kpis.LateJobCount} (now {context.Schedule.Kpis.LateJobCount}), " +
                                $"tardiness {ChatFacts.Hours(alternative.Kpis.TotalTardinessSeconds)} (now {ChatFacts.Hours(context.Schedule.Kpis.TotalTardinessSeconds)}), " +
                                $"makespan {ChatFacts.Hours(alternative.Kpis.MakespanSeconds)} (now {ChatFacts.Hours(context.Schedule.Kpis.MakespanSeconds)}).";
            }

            var settings = await _config.LoadAsync(token);
            if (!settings.IsConfigured)
                return Settled(generation, asked, new ChatTurn(ChatRole.Assistant, offlineText, NarrationSource.RuleBased, _l["Chat_SourceOffline"]), token);

            Activity = ChatActivity.AskingModel;
            var provider = ChatProviders.Create(_http, settings);
            var system = ChatFacts.BuildSystemPrompt(
                context, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, computedFacts, offlineText);
            var history = _turns.TakeLast(HistoryWindow).ToList();
            var answer = await provider.CompleteAsync(system, history, token);
            return Settled(generation, asked, new ChatTurn(ChatRole.Assistant, answer, NarrationSource.Ai, provider.Label), token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Withdraw(asked);
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately total. A provider is a third party returning JSON it
            // wrote, over a network it owns: enumerating the exception types it
            // can produce is a game this code loses once, in production, by
            // showing a red banner instead of the answer it already has. Caller
            // cancellation is handled above and stays distinct.
            if (Superseded(generation))
            {
                Withdraw(asked);
                throw new OperationCanceledException("The conversation moved to another schedule.", ex, token);
            }

            return Append(new ChatTurn(ChatRole.Assistant, offlineText, NarrationSource.RuleBased, _l["Chat_SourceOffline"],
                Note: _l["Sched_Ai_Fallback", FailureLabel(ex)]));
        }
    }

    /// <summary>Appends an answer unless the conversation moved on while it was being produced.</summary>
    private ChatTurn Settled(int generation, ChatTurn asked, ChatTurn answer, CancellationToken token)
    {
        if (!Superseded(generation))
            return Append(answer);

        Withdraw(asked);
        throw new OperationCanceledException("The conversation moved to another schedule.", null, token);
    }

    /// <summary>
    /// Takes back an unanswered question. By reference, not by index or by value:
    /// two turns carrying the same text are equal as records, so removing "the
    /// last one" or "an equal one" could take back a question that was already
    /// answered, and removing "index Count - 1" throws outright after a reset.
    /// </summary>
    private void Withdraw(ChatTurn asked)
    {
        for (int i = _turns.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_turns[i], asked))
            {
                _turns.RemoveAt(i);
                return;
            }
        }
    }

    private bool Superseded(int generation) => generation != _generation;

    private ChatTurn Append(ChatTurn turn)
    {
        _turns.Add(turn);
        return turn;
    }

    private string FailureLabel(Exception exception) => exception switch
    {
        OperationCanceledException or TimeoutException => _l["Sched_Ai_FailureTimeout"],
        HttpRequestException => _l["Sched_Ai_FailureUnavailable"],
        _ => _l["Sched_Ai_FailureResponse"]
    };

    /// <summary>Releases the gate and the conversation's cancellation source.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _conversation.Cancel();
        _conversation.Dispose();
        _gate.Dispose();
    }
}
