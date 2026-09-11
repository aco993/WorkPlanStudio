using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// The scheduling page's entry point to the assistant. It always offers an instant,
/// offline, deterministic explanation (the rule-based narrator) and, when the user
/// has configured a bring-your-own-key provider, an AI narration that <b>falls back</b>
/// to the rule-based text on any error. Concrete providers stay behind
/// <see cref="IScheduleNarrator"/>; this façade owns the choice and the fallback.
/// </summary>
public sealed class ScheduleAssistant
{
    private readonly RuleBasedNarrator _ruleBased;
    private readonly IAssistantConfig _config;
    private readonly HttpClient _http;
    private readonly IStringLocalizer<SharedResource> _l;

    public ScheduleAssistant(
        RuleBasedNarrator ruleBased,
        IAssistantConfig config,
        HttpClient http,
        IStringLocalizer<SharedResource> l)
    {
        _ruleBased = ruleBased;
        _config = config;
        _http = http;
        _l = l;
    }

    /// <summary>The instant, offline, deterministic explanation. Always available.</summary>
    public Task<NarrationResult> ExplainAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default) =>
        _ruleBased.NarrateAsync(explanation, cancellationToken);

    /// <summary>True when a usable BYOK AI provider is configured and enabled.</summary>
    public async ValueTask<bool> IsAiEnabledAsync(CancellationToken cancellationToken = default) =>
        (await _config.LoadAsync(cancellationToken)).IsConfigured;

    /// <summary>
    /// AI narration when configured; otherwise — and on any AI error — the rule-based
    /// narration, carrying a note that explains the fallback. Caller-requested
    /// cancellation is propagated instead of being presented as a provider failure.
    /// </summary>
    public async Task<NarrationResult> ExplainWithAiAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(explanation);

        var settings = await _config.LoadAsync(cancellationToken);
        if (!settings.IsConfigured)
            return await _ruleBased.NarrateAsync(explanation, cancellationToken);

        try
        {
            var ai = new AiScheduleNarrator(Chat.ChatProviders.Create(_http, settings));
            return await ai.NarrateAsync(explanation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Total by design: the narration is a nicety, the rule-based text is
            // the product. A filter listing the exception types a third-party
            // endpoint can produce is a list that is wrong exactly once — and the
            // cost of being wrong is the whole page replaced by an error card.
            var fallback = await _ruleBased.NarrateAsync(explanation, cancellationToken);
            return fallback with { Note = _l["Sched_Ai_Fallback", FailureLabel(ex)] };
        }
    }

    private string FailureLabel(Exception exception) => exception switch
    {
        OperationCanceledException or TimeoutException => _l["Sched_Ai_FailureTimeout"],
        HttpRequestException => _l["Sched_Ai_FailureUnavailable"],
        _ => _l["Sched_Ai_FailureResponse"]
    };
}
