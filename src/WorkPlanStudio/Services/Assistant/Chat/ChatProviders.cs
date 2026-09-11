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

    /// <summary>
    /// Completes the conversation and returns the model's text. An implementation
    /// either returns a non-empty answer or throws a typed failure; a response
    /// that is hostile, truncated, oversized or simply the wrong shape must
    /// never surface as an unhandled exception, because the caller's fallback
    /// is what makes the chat always answer.
    /// </summary>
    Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default);
}

/// <summary>Builds the provider the settings ask for, or throws when they are not usable.</summary>
public static class ChatProviders
{
    /// <summary>Finite provider budget; caller cancellation remains distinguishable from it.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Ceiling on a response body. The body is buffered into the WebAssembly
    /// heap, so an endpoint that streams hundreds of megabytes would kill the
    /// tab — a far worse outcome for the planner than a failed answer.
    /// </summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// Output budget sent to every provider. Without one, a runaway model bills
    /// the user's own key without bound; 1024 tokens is several times the
    /// length of any answer this prompt asks for.
    /// </summary>
    public const int MaxOutputTokens = 1024;

    /// <summary>
    /// A model name becomes a URL path segment for Gemini, so it is restricted
    /// to the characters model names actually use. <c>?</c>, <c>#</c>, <c>%</c>
    /// and <c>../</c> in a path segment change the request target — and this
    /// request carries the user's key in a header.
    /// </summary>
    public const string ModelPattern = "^[A-Za-z0-9._:@-]{1,100}$";

    /// <summary>Builds the configured provider.</summary>
    /// <param name="http">The transport; supplied by DI so tests can stub it.</param>
    /// <param name="settings">Validated bring-your-own-key settings.</param>
    /// <param name="budget">Total budget for one exchange; defaults to <see cref="RequestTimeout"/>.</param>
    public static IChatProvider Create(HttpClient http, AssistantSettings settings, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured || !settings.TryGetEndpoint(out var endpoint) || endpoint is null)
            throw new ArgumentException("AI settings are not valid.", nameof(settings));

        var window = budget ?? RequestTimeout;
        return settings.Provider switch
        {
            AssistantProvider.Anthropic => new AnthropicChatProvider(http, endpoint, settings.Model.Trim(), settings.ApiKey.Trim(), window),
            AssistantProvider.Gemini => new GeminiChatProvider(http, endpoint, settings.Model.Trim(), settings.ApiKey.Trim(), window),
            _ => new OpenAiCompatibleChatProvider(http, endpoint, settings.Model.Trim(), settings.ApiKey.Trim(), window)
        };
    }

    /// <summary>
    /// Sends the request and reads the body under <b>one</b> budget. The budget
    /// used to cover <c>SendAsync</c> only, which was harmless solely because
    /// the default completion option buffers the body inside it; reading the
    /// headers first — which is what a bounded read requires — would otherwise
    /// have moved the whole transfer outside the timeout, and a stalled stream
    /// would hang until the visitor navigated away.
    /// </summary>
    internal static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(response.Content, timeout.Token);
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidOperationException("The AI provider announced a response larger than the accepted limit.");

        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("The AI provider's response exceeded the accepted size limit.");
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    internal static string Trimmed(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? throw new InvalidOperationException("The AI provider returned an empty response.")
            : content.Trim();

    internal static string HostLabel(Uri endpoint, string model) => $"{endpoint.Host} · {model}";

    /// <summary>The endpoint as a base, with the trailing slash removed so a path can be appended.</summary>
    internal static string Base(Uri endpoint) => endpoint.ToString().TrimEnd('/');
}

/// <summary>OpenAI's <c>/chat/completions</c> shape, spoken by OpenAI, OpenRouter, Groq, Mistral, Ollama, LM Studio and others.</summary>
public sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly TimeSpan _budget;

    /// <param name="http">Transport.</param>
    /// <param name="endpoint">Validated base URL.</param>
    /// <param name="model">Model name.</param>
    /// <param name="apiKey">The user's own key; travels in the Authorization header, never in the URL.</param>
    /// <param name="budget">Total budget for one exchange, send and body read together.</param>
    public OpenAiCompatibleChatProvider(HttpClient http, Uri endpoint, string model, string apiKey, TimeSpan? budget = null)
    {
        _http = http;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _budget = budget ?? ChatProviders.RequestTimeout;
    }

    /// <inheritdoc />
    public string Label => ChatProviders.HostLabel(_endpoint, _model);

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);

        var messages = new List<ChatMessage> { new("system", system) };
        messages.AddRange(history.Select(t => new ChatMessage(t.Role == ChatRole.User ? "user" : "assistant", t.Text)));
        var payload = new ChatRequest(_model, messages, Temperature: 0.2, MaxTokens: ChatProviders.MaxOutputTokens);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatProviders.Base(_endpoint)}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        request.Content = ChatProviders.Json(JsonSerializer.Serialize(payload, AssistantJsonContext.Default.ChatRequest));

        var body = await ChatProviders.SendAsync(_http, request, _budget, cancellationToken);
        var parsed = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.ChatResponse);
        var text = parsed?.Choices?
            .FirstOrDefault(choice => !string.IsNullOrWhiteSpace(choice?.Message?.Content))?
            .Message?.Content;
        return ChatProviders.Trimmed(text);
    }
}

/// <summary>
/// Anthropic's Messages API. Called straight from the browser with the user's
/// own key, which Anthropic only allows when the request declares it
/// (<c>anthropic-dangerous-direct-browser-access</c>) — the name is the warning:
/// a key in a browser is the user's to protect, which is why it stays in
/// browser storage and is never shipped with the app.
/// </summary>
public sealed class AnthropicChatProvider : IChatProvider
{
    /// <summary>The API version this client speaks.</summary>
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly TimeSpan _budget;

    /// <param name="http">Transport.</param>
    /// <param name="endpoint">Validated base URL.</param>
    /// <param name="model">Model name.</param>
    /// <param name="apiKey">The user's own key; travels in the x-api-key header, never in the URL.</param>
    /// <param name="budget">Total budget for one exchange, send and body read together.</param>
    public AnthropicChatProvider(HttpClient http, Uri endpoint, string model, string apiKey, TimeSpan? budget = null)
    {
        _http = http;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _budget = budget ?? ChatProviders.RequestTimeout;
    }

    /// <inheritdoc />
    public string Label => ChatProviders.HostLabel(_endpoint, _model);

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);

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

        var payload = new AnthropicRequest(_model, ChatProviders.MaxOutputTokens, leading.ToString(), turns);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatProviders.Base(_endpoint)}/v1/messages");
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
        request.Content = ChatProviders.Json(JsonSerializer.Serialize(payload, AssistantJsonContext.Default.AnthropicRequest));

        var body = await ChatProviders.SendAsync(_http, request, _budget, cancellationToken);
        var parsed = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.AnthropicResponse);
        var blocks = parsed?.Content?
            .Where(block => block?.Type == "text" && !string.IsNullOrEmpty(block.Text))
            .Select(block => block!.Text!)
            .ToList();
        return ChatProviders.Trimmed(blocks is { Count: > 0 } ? string.Join("\n", blocks) : null);
    }
}

/// <summary>Google's Gemini API, <c>models/{model}:generateContent</c>, key in a header rather than the query string.</summary>
public sealed class GeminiChatProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly TimeSpan _budget;

    /// <param name="http">Transport.</param>
    /// <param name="endpoint">Validated base URL.</param>
    /// <param name="model">Model name; escaped before it becomes a path segment.</param>
    /// <param name="apiKey">The user's own key; travels in the x-goog-api-key header, never in the URL.</param>
    /// <param name="budget">Total budget for one exchange, send and body read together.</param>
    public GeminiChatProvider(HttpClient http, Uri endpoint, string model, string apiKey, TimeSpan? budget = null)
    {
        _http = http;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _budget = budget ?? ChatProviders.RequestTimeout;
    }

    /// <inheritdoc />
    public string Label => ChatProviders.HostLabel(_endpoint, _model);

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);

        var contents = history
            .Select(t => new GeminiContent(t.Role == ChatRole.User ? "user" : "model", [new GeminiPart(t.Text)]))
            .ToList();
        var payload = new GeminiRequest(
            new GeminiContent(null, [new GeminiPart(system)]),
            contents,
            new GeminiGenerationConfig(ChatProviders.MaxOutputTokens));

        // The model name is free text the user typed. Unescaped it is a path
        // segment, so "x?key=leak" turns the rest of the path into a query on a
        // request that carries the key in a header — the accidental credential
        // routing the endpoint validator rejects for the endpoint itself.
        var model = Uri.EscapeDataString(_model);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChatProviders.Base(_endpoint)}/models/{model}:generateContent");
        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        request.Content = ChatProviders.Json(JsonSerializer.Serialize(payload, AssistantJsonContext.Default.GeminiRequest));

        var body = await ChatProviders.SendAsync(_http, request, _budget, cancellationToken);
        var parsed = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.GeminiResponse);
        var parts = parsed?.Candidates?
            .FirstOrDefault(candidate => candidate?.Content?.Parts is { Count: > 0 })?
            .Content?.Parts?
            .Where(part => !string.IsNullOrEmpty(part?.Text))
            .Select(part => part!.Text!)
            .ToList();
        return ChatProviders.Trimmed(parts is { Count: > 0 } ? string.Join("\n", parts) : null);
    }
}
