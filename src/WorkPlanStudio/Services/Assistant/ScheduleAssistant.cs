using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
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
    private readonly BackendState? _backend;
    private readonly ServerSession? _server;

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

    public ScheduleAssistant(
        RuleBasedNarrator ruleBased,
        IAssistantConfig config,
        HttpClient http,
        IStringLocalizer<SharedResource> l,
        BackendState backend,
        ServerSession server)
    {
        _ruleBased = ruleBased;
        _config = config;
        _http = http;
        _l = l;
        _backend = backend;
        _server = server;
    }

    /// <summary>The instant, offline, deterministic explanation. Always available.</summary>
    public Task<NarrationResult> ExplainAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default) =>
        _ruleBased.NarrateAsync(explanation, cancellationToken);

    public bool UsesServerProvider => _backend?.Mode == BackendMode.Server;

    /// <summary>True when a usable BYOK AI provider is configured and enabled.</summary>
    public async ValueTask<bool> IsAiEnabledAsync()
    {
        if (_backend?.Mode != BackendMode.Server)
            return (await _config.LoadAsync()).IsConfigured;
        try { return (await _server!.GetFromJsonAsync<WorkPlanStudio.Contracts.AssistantStatusDto>("api/assistant/status"))?.Enabled == true; }
        catch (HttpRequestException) { return false; }
    }

    /// <summary>
    /// AI narration when configured; otherwise — and on any AI error — the rule-based
    /// narration, carrying a note that explains the fallback. Caller-requested
    /// cancellation is propagated instead of being presented as a provider failure.
    /// </summary>
    public async Task<NarrationResult> ExplainWithAiAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(explanation);

        if (_backend?.Mode == BackendMode.Server)
            return await ExplainWithServerAiAsync(explanation, cancellationToken);

        var settings = await _config.LoadAsync();
        if (!settings.IsConfigured)
            return await _ruleBased.NarrateAsync(explanation, cancellationToken);

        try
        {
            var ai = new OpenAiScheduleNarrator(_http, settings);
            return await ai.NarrateAsync(explanation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or OperationCanceledException or JsonException
               or InvalidOperationException or NotSupportedException or UriFormatException)
        {
            var fallback = await _ruleBased.NarrateAsync(explanation, cancellationToken);
            return fallback with { Note = _l["Sched_Ai_Fallback", FailureLabel(ex)] };
        }
    }

    private async Task<NarrationResult> ExplainWithServerAiAsync(ScheduleExplanation explanation, CancellationToken cancellationToken)
    {
        try
        {
            var request = new WorkPlanStudio.Contracts.AssistantNarrationRequest(
                AssistantPrompt.BuildFacts(explanation), CultureInfo.CurrentUICulture.Name);
            using var response = await _server!.SendAsync(HttpMethod.Post, "api/assistant/narrate", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Assistant server returned {(int)response.StatusCode}.");
            var result = await response.Content.ReadFromJsonAsync<WorkPlanStudio.Contracts.AssistantNarrationDto>(cancellationToken)
                ?? throw new InvalidOperationException("Assistant server returned no narration.");
            return new NarrationResult(result.Lines.Select(line => new NarrationLine(line, FindingTone.Info)).ToArray(), NarrationSource.Ai, result.SourceLabel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            var fallback = await _ruleBased.NarrateAsync(explanation, cancellationToken);
            return fallback with { Note = _l["Sched_Ai_Fallback", FailureLabel(ex)] };
        }
    }

    private string FailureLabel(Exception exception) => exception switch
    {
        OperationCanceledException => _l["Sched_Ai_FailureTimeout"],
        JsonException or InvalidOperationException or NotSupportedException => _l["Sched_Ai_FailureResponse"],
        _ => _l["Sched_Ai_FailureUnavailable"]
    };
}
