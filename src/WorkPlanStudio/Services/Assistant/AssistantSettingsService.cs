using System.Text.Json;
using Microsoft.JSInterop;

namespace WorkPlanStudio.Services;

/// <summary>
/// Persists <see cref="AssistantSettings"/> in the browser's <c>localStorage</c>
/// (via the <c>workplanSettings</c> JS helper), caching the value for the session.
/// The API key never leaves the browser except in requests to the user's own
/// configured endpoint.
/// </summary>
public sealed class AssistantSettingsService : IAssistantConfig
{
    private const string Key = "assistant";

    private readonly IJSRuntime _js;
    private AssistantSettings? _cache;

    public AssistantSettingsService(IJSRuntime js) => _js = js;

    /// <inheritdoc />
    public async ValueTask<AssistantSettings> LoadAsync()
    {
        if (_cache is not null)
            return _cache;

        var json = await _js.InvokeAsync<string?>("workplanSettings.get", Key);
        _cache = Deserialize(json);
        return _cache;
    }

    /// <inheritdoc />
    public async Task SaveAsync(AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _cache = settings;
        var json = JsonSerializer.Serialize(settings, AssistantJsonContext.Default.AssistantSettings);
        await _js.InvokeVoidAsync("workplanSettings.set", Key, json);
    }

    private static AssistantSettings Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return AssistantSettings.Default;

        try
        {
            return JsonSerializer.Deserialize(json, AssistantJsonContext.Default.AssistantSettings)
                ?? AssistantSettings.Default;
        }
        catch (JsonException)
        {
            // Corrupt or outdated stored value — fall back to defaults rather than fail.
            return AssistantSettings.Default;
        }
    }
}
