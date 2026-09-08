using System.Text;
using System.Text.Json;

namespace WorkPlanStudio.Services.Chat;

/// <summary>
/// A model that can answer a conversation. Three implementations sit behind
/// this seam — OpenAI-compatible, Anthropic and Gemini — so the narrator and
/// the chat never depend on a provider's wire format.
/// </summary>
public interface IChatProvider
{
    /// <summary>Short label shown next to an answer, e.g. "api.anthropic.com · claude-opus-5".</summary>
    string Label { get; }

    /// <summary>Completes the conversation and returns the model's text.</summary>
    Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default);
}

/// <summary>Builds the provider the settings ask for, or throws when they are not usable.</summary>
public static class ChatProviders
{
    /// <summary>Finite provider budget; caller cancellation remains distinguishable from it.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static IChatProvider Create(HttpClient http, AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured || !settings.TryGetEndpoint(out var endpoint) || endpoint is null)
            throw new ArgumentException("AI settings are not valid.", nameof(settings));

        return settings.Provider switch
        {
            AssistantProvider.Anthropic => new AnthropicChatProvider(http, endpoint, settings.Model.Trim(), settings.ApiKey.Trim()),
            AssistantProvider.Gemini => new GeminiChatProvider(http, endpoint, settings.Model.Trim(), settings.ApiKey.Trim()),
            _ => new OpenAiCompatibleChatProvider(http, endpoint, settings.Model.Trim(), settings.ApiKey.Trim())
        };
    }

    internal static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    internal static string Trimmed(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? throw new InvalidOperationException("The AI provider returned an empty response.")
            : content.Trim();

    internal static string HostLabel(Uri endpoint, string model) => $"{endpoint.Host} · {model}";
}

/// <summary>OpenAI's <c>/chat/completions</c> shape, spoken by OpenAI, OpenRouter, Groq, Mistral, Ollama, LM Studio and others.</summary>
public sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiCompatibleChatProvider(HttpClient http, Uri endpoint, string model, string apiKey)
    {
        _http = http;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
    }

    public string Label => ChatProviders.HostLabel(_endpoint, _model);

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage> { new("system", system) };
        messages.AddRange(history.Select(t => new ChatMessage(t.Role == ChatRole.User ? "user" : "assistant", t.Text)));
        var payload = new ChatRequest(_model, messages, Temperature: 0.2);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint.ToString().TrimEnd('/')}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        request.Content = ChatProviders.Json(JsonSerializer.Serialize(payload, AssistantJsonContext.Default.ChatRequest));

        var body = await ChatProviders.SendAsync(_http, request, cancellationToken);
        var parsed = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.ChatResponse);
        return ChatProviders.Trimmed(parsed?.Choices is { Count: > 0 } ? parsed.Choices[0].Message.Content : null);
    }
}

/// <summary>
/// Anthropic's Messages API. Called straight from the browser with the user's
/// own key, which Anthropic only allows when the request declares it
/// (<c>anthropic-dangerous-direct-browser-access</c>) — the name is the warning:
/// a key in a browser is the user's to protect, which is why it stays in
/// <c>localStorage</c> and is never shipped with the app.
/// </summary>
public sealed class AnthropicChatProvider : IChatProvider
{
    /// <summary>The API version this client speaks.</summary>
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public AnthropicChatProvider(HttpClient http, Uri endpoint, string model, string apiKey)
    {
        _http = http;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
    }

    public string Label => ChatProviders.HostLabel(_endpoint, _model);

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default)
    {
        // The Messages API wants strictly alternating user/assistant turns that
        // start with the user; a leading assistant narration is folded into the
        // system prompt instead of being dropped.
        var turns = new List<AnthropicMessage>();
        var leading = new StringBuilder(system);
        foreach (var turn in history)
        {
            var role = turn.Role == ChatRole.User ? "user" : "assistant";
            if (turns.Count == 0 && role == "assistant")
            {
                leading.AppendLine().AppendLine("Earlier assistant note: " + turn.Text);
                continue;
            }

            if (turns.Count > 0 && turns[^1].Role == role)
                turns[^1] = turns[^1] with { Content = turns[^1].Content + "\n\n" + turn.Text };
            else
                turns.Add(new AnthropicMessage(role, turn.Text));
        }

        var payload = new AnthropicRequest(_model, MaxTokens: 1024, leading.ToString(), turns);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint.ToString().TrimEnd('/')}/v1/messages");
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
        request.Content = ChatProviders.Json(JsonSerializer.Serialize(payload, AssistantJsonContext.Default.AnthropicRequest));

        var body = await ChatProviders.SendAsync(_http, request, cancellationToken);
        var parsed = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.AnthropicResponse);
        var text = parsed?.Content is { Count: > 0 }
            ? string.Join("\n", parsed.Content.Where(c => c.Type == "text").Select(c => c.Text))
            : null;
        return ChatProviders.Trimmed(text);
    }
}

/// <summary>Google's Gemini API, <c>models/{model}:generateContent</c>, key in a header rather than the query string.</summary>
public sealed class GeminiChatProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public GeminiChatProvider(HttpClient http, Uri endpoint, string model, string apiKey)
    {
        _http = http;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
    }

    public string Label => ChatProviders.HostLabel(_endpoint, _model);

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default)
    {
        var contents = history
            .Select(t => new GeminiContent(t.Role == ChatRole.User ? "user" : "model", [new GeminiPart(t.Text)]))
            .ToList();
        var payload = new GeminiRequest(new GeminiContent(null, [new GeminiPart(system)]), contents);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint.ToString().TrimEnd('/')}/models/{_model}:generateContent");
        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        request.Content = ChatProviders.Json(JsonSerializer.Serialize(payload, AssistantJsonContext.Default.GeminiRequest));

        var body = await ChatProviders.SendAsync(_http, request, cancellationToken);
        var parsed = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.GeminiResponse);
        var text = parsed?.Candidates is { Count: > 0 } && parsed.Candidates[0].Content?.Parts is { Count: > 0 } parts
            ? string.Join("\n", parts.Select(p => p.Text))
            : null;
        return ChatProviders.Trimmed(text);
    }
}
