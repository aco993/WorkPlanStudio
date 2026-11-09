using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Components;

/// <summary>
/// The assistant's provider settings. The API key is write-only from here: the
/// field is never filled from storage, a blank field means "keep the stored
/// key", and the dialog learns only whether one exists.
/// <para>
/// Opened by <see cref="OpenAsync"/> rather than a bound flag, because opening
/// is not a state the owner holds — it is a load from storage that has to
/// finish before the form is worth showing.
/// </para>
/// </summary>
public sealed partial class AiSettingsDialog
{
    [Inject] private IAssistantConfig AssistantConfig { get; set; } = null!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = null!;

    /// <summary>Raised after a save or a forgotten key, so the owner can ask again whether the assistant is enabled.</summary>
    [Parameter] public EventCallback SettingsChanged { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly AiForm _form = new();
    private bool _visible;
    private string? _error;

    private bool _keyStored;
    private string? _keyNotice;

    public async Task OpenAsync()
    {
        var settings = await AssistantConfig.LoadAsync(_lifetime.Token);
        _form.Enabled = settings.Enabled;
        _form.Provider = settings.Provider;
        _form.PreviousProvider = settings.Provider;
        _form.Endpoint = settings.Endpoint;
        _form.Model = settings.Model;

        // The stored key never re-enters the page; the placeholder says whether
        // one exists, which is all the dialog needs to be usable.
        _form.ApiKey = "";
        _keyStored = await AssistantConfig.HasKeyForAsync(settings.Provider, _lifetime.Token);
        _keyNotice = null;
        _error = null;
        _visible = true;

        // Called from the panel's handler, which re-renders the panel and not
        // this component: nothing it receives as a parameter has changed.
        StateHasChanged();
    }

    private async Task ForgetKeyAsync()
    {
        await AssistantConfig.ForgetApiKeyAsync(_lifetime.Token);
        _form.ApiKey = "";
        _keyStored = false;
        _keyNotice = L["Ai_KeyForgotten", _form.Provider];
        await SettingsChanged.InvokeAsync();
    }

    /// <summary>Switching provider fills in that provider's endpoint and model unless the user typed their own.</summary>
    private async Task ProviderChanged()
    {
        var previous = _form.PreviousProvider;
        if (string.IsNullOrWhiteSpace(_form.Endpoint) || _form.Endpoint == AssistantSettings.DefaultEndpoint(previous))
            _form.Endpoint = AssistantSettings.DefaultEndpoint(_form.Provider);
        if (string.IsNullOrWhiteSpace(_form.Model) || _form.Model == AssistantSettings.DefaultModel(previous))
            _form.Model = AssistantSettings.DefaultModel(_form.Provider);
        _form.PreviousProvider = _form.Provider;

        // Keys are stored per provider, so switching provider switches which key
        // applies. Ask the store rather than guess: a wrong indicator here either
        // hides a key the user has or promises one they do not.
        _form.ApiKey = "";
        _keyNotice = null;
        _keyStored = await AssistantConfig.HasKeyForAsync(_form.Provider, _lifetime.Token);
    }

    private async Task SaveAsync()
    {
        var settings = new AssistantSettings
        {
            Enabled = _form.Enabled,
            Provider = _form.Provider,
            Endpoint = _form.Endpoint.Trim(),
            Model = _form.Model.Trim(),
            ApiKey = _form.ApiKey.Trim()
        };

        // A blank key means "keep the stored one", so completeness is judged
        // without it: the settings are incomplete only when nothing is stored.
        if (settings.Enabled && !(settings.IsUsableApartFromTheKey && (settings.HasApiKey || _keyStored)))
        {
            _error = L["Sched_Ai_InvalidSettings"];
            return;
        }

        await AssistantConfig.SaveAsync(settings, _lifetime.Token);
        await SettingsChanged.InvokeAsync();
        _visible = false;
    }

    /// <summary>Mutable view-model for the AI assistant settings dialog.</summary>
    private sealed class AiForm
    {
        public bool Enabled { get; set; }
        public AssistantProvider Provider { get; set; } = AssistantProvider.OpenAiCompatible;
        public AssistantProvider PreviousProvider { get; set; } = AssistantProvider.OpenAiCompatible;
        public string Endpoint { get; set; } = AssistantSettings.DefaultEndpoint(AssistantProvider.OpenAiCompatible);
        public string Model { get; set; } = AssistantSettings.DefaultModel(AssistantProvider.OpenAiCompatible);
        public string ApiKey { get; set; } = "";
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
