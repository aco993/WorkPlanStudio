using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Services;

/// <summary>
/// Loads and saves the AI narrator's <see cref="AssistantSettings"/>. Abstracted so
/// the assistant and its tests do not depend on the browser storage implementation.
/// </summary>
public interface IAssistantConfig
{
    /// <summary>Returns the current settings (defaults when nothing is stored).</summary>
    ValueTask<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="settings"/>. A blank <see cref="AssistantSettings.ApiKey"/>
    /// means "leave the stored key alone", not "delete it": the settings dialog
    /// must be able to change the endpoint or the model without ever holding the
    /// secret, which is what keeps the key out of the page's DOM. Removing a key
    /// is <see cref="ForgetApiKeyAsync"/> — an explicit act, never a side effect
    /// of saving.
    /// </summary>
    Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Forgets the key stored for the current provider. The rest of the settings stay.</summary>
    Task ForgetApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a key is stored for <paramref name="provider"/>, without returning
    /// it. The settings dialog needs to tell the user "a key is already stored"
    /// while never holding the key itself — asking this instead of reading the
    /// value is what keeps the secret out of the page.
    /// </summary>
    /// <param name="provider">The provider whose slot to probe.</param>
    /// <param name="cancellationToken">Cancels the storage read.</param>
    ValueTask<bool> HasKeyForAsync(AssistantProvider provider, CancellationToken cancellationToken = default);
}
