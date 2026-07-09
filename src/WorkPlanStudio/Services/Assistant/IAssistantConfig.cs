namespace WorkPlanStudio.Services;

/// <summary>
/// Loads and saves the AI narrator's <see cref="AssistantSettings"/>. Abstracted so
/// the assistant and its tests do not depend on the browser storage implementation.
/// </summary>
public interface IAssistantConfig
{
    /// <summary>Returns the current settings (defaults when nothing is stored).</summary>
    ValueTask<AssistantSettings> LoadAsync();

    /// <summary>Persists <paramref name="settings"/>.</summary>
    Task SaveAsync(AssistantSettings settings);
}
