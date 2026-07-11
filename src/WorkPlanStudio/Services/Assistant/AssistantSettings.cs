namespace WorkPlanStudio.Services;

/// <summary>
/// Bring-your-own-key configuration for the optional AI narrator.
/// <para>
/// It is <b>disabled by default</b>: with no key the app always uses the built-in
/// rule-based narrator, so the public demo works with nothing to configure. When
/// enabled, the key is stored only in the browser's <c>localStorage</c> (never in
/// the repository) and is sent only to the endpoint the user configures.
/// </para>
/// </summary>
public sealed record AssistantSettings
{
    /// <summary>Whether the AI narrator is switched on.</summary>
    public bool Enabled { get; init; }

    /// <summary>Base URL of an OpenAI-compatible API (must expose <c>/chat/completions</c>).</summary>
    public string Endpoint { get; init; } = "https://api.openai.com/v1";

    /// <summary>The chat model to request.</summary>
    public string Model { get; init; } = "gpt-4o-mini";

    /// <summary>The API key. Held only client-side; never logged or committed.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>True when the AI narrator is enabled and has everything it needs to run.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Model);

    /// <summary>The default (disabled) settings.</summary>
    public static AssistantSettings Default => new();
}
