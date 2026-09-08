using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Services;

/// <summary>
/// Bring-your-own-key configuration for the optional AI assistant.
/// <para>
/// It is <b>disabled by default</b>: with no key the app always answers on-device,
/// so the public demo works with nothing to configure. When enabled, the key is
/// stored only in the browser's <c>localStorage</c> (never in the repository) and
/// is sent only to the endpoint the user configures, in the format that
/// provider expects.
/// </para>
/// </summary>
public sealed record AssistantSettings
{
    /// <summary>Whether the AI assistant is switched on.</summary>
    public bool Enabled { get; init; }

    /// <summary>Which API the endpoint speaks.</summary>
    public AssistantProvider Provider { get; init; } = AssistantProvider.OpenAiCompatible;

    /// <summary>Base URL of the provider's API.</summary>
    public string Endpoint { get; init; } = DefaultEndpoint(AssistantProvider.OpenAiCompatible);

    /// <summary>The model to request.</summary>
    public string Model { get; init; } = DefaultModel(AssistantProvider.OpenAiCompatible);

    /// <summary>The API key. Held only client-side; never logged or committed.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>True when the assistant is enabled and has everything it needs to run.</summary>
    public bool IsConfigured =>
        Enabled
        && Enum.IsDefined(Provider)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && ApiKey.Length <= 4096
        && !string.IsNullOrWhiteSpace(Model)
        && Model.Trim().Length <= 100
        && TryGetEndpoint(out _);

    /// <summary>Accepts HTTPS endpoints, plus HTTP loopback for local development.</summary>
    public bool TryGetEndpoint(out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out var candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
            return false;

        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !(string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && candidate.IsLoopback))
            return false;

        endpoint = candidate;
        return true;
    }

    /// <summary>The default (disabled) settings.</summary>
    public static AssistantSettings Default => new();

    /// <summary>Where each provider's API lives by default.</summary>
    public static string DefaultEndpoint(AssistantProvider provider) => provider switch
    {
        AssistantProvider.Anthropic => "https://api.anthropic.com",
        AssistantProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta",
        _ => "https://api.openai.com/v1"
    };

    /// <summary>A sensible current model for each provider.</summary>
    public static string DefaultModel(AssistantProvider provider) => provider switch
    {
        AssistantProvider.Anthropic => "claude-opus-5",
        AssistantProvider.Gemini => "gemini-2.5-flash",
        _ => "gpt-4o-mini"
    };
}
