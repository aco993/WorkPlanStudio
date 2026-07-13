using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Optional AI narrator: posts the structured facts to an OpenAI-compatible
/// <c>/chat/completions</c> endpoint and returns the model's prose. Constructed per
/// call with the user's current settings; failures are the caller's concern
/// (<see cref="ScheduleAssistant"/> falls back to the rule-based narrator), so this
/// class stays a thin, single-purpose HTTP client.
/// </summary>
public sealed class OpenAiScheduleNarrator : IScheduleNarrator
{
    /// <summary>Finite provider budget; caller cancellation remains distinguishable.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly AssistantSettings _settings;
    private readonly string _language;

    public OpenAiScheduleNarrator(HttpClient http, AssistantSettings settings, string? language = null)
    {
        if (!settings.IsConfigured || !settings.TryGetEndpoint(out _))
            throw new ArgumentException("AI narrator settings are not valid.", nameof(settings));

        _http = http;
        _settings = settings;
        _language = string.IsNullOrWhiteSpace(language)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language;
    }

    /// <inheritdoc />
    public string SourceLabel => $"{Host(_settings.Endpoint)} · {_settings.Model}";

    /// <inheritdoc />
    public async Task<NarrationResult> NarrateAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(explanation);

        var payload = new ChatRequest(
            _settings.Model,
            new[]
            {
                new ChatMessage("system", $"{AssistantPrompt.System} Respond in the language with ISO code '{_language}'."),
                new ChatMessage("user", AssistantPrompt.BuildFacts(explanation))
            },
            Temperature: 0.2);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.Endpoint.TrimEnd('/')}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.ApiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, AssistantJsonContext.Default.ChatRequest),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.ChatResponse);
        var content = parsed?.Choices is { Count: > 0 } ? parsed.Choices[0].Message.Content : null;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("The AI provider returned an empty response.");

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new NarrationLine(StripBullet(line), FindingTone.Info))
            .ToList();

        return new NarrationResult(lines, NarrationSource.Ai, SourceLabel);
    }

    private static string StripBullet(string line) => line.TrimStart('-', '*', '•', ' ', '\t');

    private static string Host(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : endpoint;
}
