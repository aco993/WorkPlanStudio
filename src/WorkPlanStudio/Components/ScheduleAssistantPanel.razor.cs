using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Components;

/// <summary>
/// What the schedule on screen means, in sentences: the offline narration of the
/// run, the optional model-written one, and the conversation about it.
/// <para>
/// Everything here is keyed to the run on screen, and the run is identified by
/// its <see cref="Parameters"/> object: the page takes a fresh one from the form
/// for every run and hands it in only when the run succeeds. A new run is the
/// only event that re-narrates and re-points the conversation; a cancelled run
/// hands back the same object, so the narration and the thread survive it
/// without the page saving and restoring them — which used to be a
/// <c>previousNarration</c> local in the page's run method.
/// </para>
/// <para>
/// Not the <see cref="Result"/> object: a scheduler is free to hand back the same
/// instance for the same answer, and a regenerated schedule must still start a
/// fresh conversation. <see cref="OptimalityProofCard"/> keys on the same object.
/// </para>
/// </summary>
public sealed partial class ScheduleAssistantPanel
{
    [Inject] private ScheduleAssistant Assistant { get; set; } = null!;
    [Inject] private ScheduleChat Chat { get; set; } = null!;
    [Inject] private PlantSettingsService PlantSettings { get; set; } = null!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = null!;
    [Inject] private ILogger<ScheduleAssistantPanel> Logger { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    /// <summary>The schedule on screen.</summary>
    [Parameter, EditorRequired] public ScheduleResult Result { get; set; } = null!;

    /// <summary>The parameters that produced <see cref="Result"/>; a what-if question re-runs from these.</summary>
    [Parameter, EditorRequired] public SchedulingParameters Parameters { get; set; } = null!;

    /// <summary>
    /// A run is in flight. The narration gives way to a spinner, because it is
    /// about to describe a schedule that is being replaced, and the chat is
    /// closed to new questions for the same reason.
    /// </summary>
    [Parameter] public bool Running { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private AiSettingsDialog SettingsDialog { get; set; } = null!;
    private SchedulingParameters? _narrated;

    /// <summary>Not shown while <see cref="Running"/>: it describes a schedule that is being replaced.</summary>
    private NarrationResult? _narration;
    private bool _aiEnabled;
    private bool _aiBusy;

    private string _question = "";
    private bool _chatBusy;
    private string? _chatError;

    /// <summary>Something the reader should know that is not a failure; see the supersede case in <see cref="AskAsync"/>.</summary>
    private string? _chatNotice;
    private bool _scrollChat;

    protected override async Task OnInitializedAsync() =>
        _aiEnabled = await Assistant.IsAiEnabledAsync(_lifetime.Token);

    protected override async Task OnParametersSetAsync()
    {
        if (ReferenceEquals(_narrated, Parameters))
            return;
        _narrated = Parameters;

        // Instant, offline, deterministic explanation of the run just produced.
        _narration = Result.Explanation is null ? null : await Assistant.ExplainAsync(Result.Explanation);

        // An old conversation would describe an old schedule.
        await ResetChatAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_scrollChat)
            return;
        _scrollChat = false;
        try
        {
            await JS.InvokeVoidAsync("workplanChat.scrollToEnd");
        }
        catch (JSException)
        {
            // cosmetic; the answer is on the page either way
        }
    }

    private async Task RefreshAiEnabledAsync() =>
        _aiEnabled = await Assistant.IsAiEnabledAsync(_lifetime.Token);

    private async Task AskAiAsync()
    {
        if (Result.Explanation is null)
            return;

        _aiBusy = true;
        StateHasChanged();
        try
        {
            _narration = await Assistant.ExplainWithAiAsync(Result.Explanation);
        }
        finally
        {
            _aiBusy = false;
        }
    }

    /// <summary>Points the conversation at the run just produced; an old conversation would describe an old schedule.</summary>
    private async Task ResetChatAsync()
    {
        _chatError = null;
        _chatNotice = null;
        if (Result is not { HasData: true, Horizon: { } horizon })
        {
            Chat.Reset(null);
            return;
        }

        var settings = await PlantSettings.GetAsync(_lifetime.Token);
        var rules = settings.ToRules();
        var holidays = GermanHolidays.Between(
            DateOnly.FromDateTime(horizon),
            DateOnly.FromDateTime(horizon.AddSeconds(Result.MakespanSeconds)),
            rules.State, rules.IncludePartialHolidays);
        Chat.Reset(new ScheduleChatContext(Result, Parameters, rules, holidays));
    }

    private Task SubmitQuestionAsync() => AskAsync(_question);

    private async Task AskAsync(string question)
    {
        // Every one of these is already disabled in the markup; a keyboard
        // activation against a render that has not caught up yet can still
        // arrive here, the same way it can at Generate. Running is the one that
        // matters most: the run about to land will point the conversation at a
        // different schedule, so a question let through now is answered into a
        // thread that is about to be cleared.
        if (_chatBusy || Chat.IsBusy || Running || string.IsNullOrWhiteSpace(question))
            return;

        _chatBusy = true;
        _chatError = null;
        _chatNotice = null;
        _question = "";
        StateHasChanged();
        try
        {
            await Chat.AskAsync(question, _lifetime.Token);
            _scrollChat = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Navigated away; there is nothing left to render into.
        }
        catch (OperationCanceledException)
        {
            // Not the page going away: a run landed while this question was in
            // flight and pointed the conversation at the new schedule, so the
            // answer would have described a plan that is no longer on screen.
            // The chat controls are disabled for the length of a run precisely
            // so this is hard to reach, but a question started a moment before
            // Generate is still in flight when the run resets the thread.
            //
            // Say so rather than re-ask. Re-asking would spend a model call the
            // reader did not ask for a second time, and a what-if would re-run
            // the scheduler again seconds after a run just finished; worse, the
            // question may not even mean the same thing about the new plan.
            // Dropping it silently is what made a question asked during a run
            // vanish with no trace at all.
            _chatNotice = L["Chat_SupersededByNewRun"];
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Chat question failed");
            _chatError = L["Error_ScheduleFailed"];
        }
        finally
        {
            _chatBusy = false;
        }
    }

    private void ClearChat()
    {
        Chat.Reset(Chat.Context);
        _chatError = null;
        _chatNotice = null;
    }

    private static string ToneClass(FindingTone tone) => tone switch
    {
        FindingTone.Good => "tone-good",
        FindingTone.Info => "tone-info",
        _ => "tone-warning"
    };

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
