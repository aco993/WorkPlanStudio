using System.Text.Json;
using Microsoft.JSInterop;
using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Services;

/// <summary>
/// Persists <see cref="AssistantSettings"/> in the browser (via the
/// <c>workplanSettings</c> helper), caching the value for the session.
/// <para>
/// The key is stored <b>apart from the rest of the settings and per provider</b>,
/// under <c>assistant.key.&lt;provider&gt;</c>. Two things follow. The settings
/// blob no longer contains a secret, so it can be read or exported without
/// leaking one. And switching provider no longer carries the key entered for one
/// host to a different host — the old shape kept a single key field, so choosing
/// a different provider silently pointed an OpenAI key at Google.
/// </para>
/// <para>
/// This is browser storage, not a vault: script running on this origin can read
/// it, as can a browser extension with host access. That is a property of a
/// static site with no backend and is stated in the settings dialog, in
/// <c>SECURITY.md</c> and in <c>docs/adr/0025-hostile-input-on-the-model-path.md</c>.
/// </para>
/// </summary>
public sealed class AssistantSettingsService : IAssistantConfig
{
    private const string SettingsKey = "assistant";
    private const string KeyPrefix = "assistant.key.";

    private readonly IJSRuntime _js;
    private AssistantSettings? _cache;

    public AssistantSettingsService(IJSRuntime js) => _js = js;

    /// <inheritdoc />
    public async ValueTask<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
            return _cache;

        var json = await _js.InvokeAsync<string?>("workplanSettings.get", cancellationToken, SettingsKey);
        var settings = Deserialize(json);
        var key = await ReadKeyAsync(settings.Provider, cancellationToken);

        // A key stored by an earlier version lives inside the settings blob. Move
        // it to its own slot and rewrite the blob without it, so the clear-text
        // copy in the shared value does not outlive the upgrade.
        if (key.Length == 0 && LegacyKey(json) is { Length: > 0 } legacy)
        {
            key = legacy;
            await WriteAsync(KeyFor(settings.Provider), key, cancellationToken);
            await WriteAsync(SettingsKey, Serialize(settings), cancellationToken);
        }

        _cache = settings with { ApiKey = key };
        return _cache;
    }

    /// <inheritdoc />
    public async Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var typed = settings.ApiKey?.Trim() ?? "";
        var effective = typed.Length > 0 ? typed : await ReadKeyAsync(settings.Provider, cancellationToken);

        await WriteAsync(SettingsKey, Serialize(settings), cancellationToken);
        if (typed.Length > 0)
            await WriteAsync(KeyFor(settings.Provider), typed, cancellationToken);

        _cache = settings with { ApiKey = effective };
    }

    /// <inheritdoc />
    public async Task ForgetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken);
        await RemoveAsync(KeyFor(current.Provider), cancellationToken);
        _cache = current with { ApiKey = "" };
    }

    /// <inheritdoc />
    public async ValueTask<bool> HasKeyForAsync(AssistantProvider provider, CancellationToken cancellationToken = default) =>
        (await ReadKeyAsync(provider, cancellationToken)).Length > 0;

    private static string KeyFor(AssistantProvider provider) => KeyPrefix + provider;

    private async ValueTask<string> ReadKeyAsync(AssistantProvider provider, CancellationToken cancellationToken) =>
        (await _js.InvokeAsync<string?>("workplanSettings.get", cancellationToken, KeyFor(provider)))?.Trim() ?? "";

    private ValueTask WriteAsync(string name, string value, CancellationToken cancellationToken) =>
        _js.InvokeVoidAsync("workplanSettings.set", cancellationToken, name, value);

    private ValueTask RemoveAsync(string name, CancellationToken cancellationToken) =>
        _js.InvokeVoidAsync("workplanSettings.remove", cancellationToken, name);

    private static string Serialize(AssistantSettings settings) =>
        JsonSerializer.Serialize(settings, AssistantJsonContext.Default.AssistantSettings);

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

    /// <summary>Reads a key out of a pre-split settings blob, ignoring anything that is not one.</summary>
    private static string? LegacyKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("ApiKey", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
