using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Chat;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// A question asked while a run is in flight used to disappear. The run ends,
/// the page points the conversation at the new schedule, the in-flight question
/// is superseded — correctly, an answer about the old plan would mislead — and
/// the page swallowed the cancellation as "the page is going away". No answer,
/// no message, nothing in the thread.
/// <para>
/// It surfaced as an intermittent test failure under parallel load, which is the
/// same thing a user meets on a slow machine: the suggestion chips are on screen
/// before the first run has finished.
/// </para>
/// </summary>
public sealed class ChatDuringARunTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private FakeScheduleService Arrange(HttpClient? client = null, AssistantSettings? assistant = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var scheduler = new FakeScheduleService { Result = Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) } };
        Services.AddSingleton<IProductionScheduleService>(scheduler);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton<RuleBasedNarrator>();
        Services.AddSingleton<IAssistantConfig>(new FakeAssistantConfig { Settings = assistant ?? AssistantSettings.Default });
        Services.AddSingleton(client ?? new HttpClient());
        Services.AddSingleton<ScheduleAssistant>();
        Services.AddSingleton<OfflineScheduleAnswerer>();
        Services.AddSingleton<ScheduleChat>();
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("chat-run.db", new FakeStorage())));
        Services.AddScheduleExport();
        Services.AddOptimalityProver();
        return scheduler;
    }

    /// <summary>
    /// <c>Assert.All</c> passes vacuously on an empty collection, which would make
    /// "every chip is enabled" true before any chip exists. This makes the wait
    /// mean what it says.
    /// </summary>
    private static IReadOnlyList<AngleSharp.Dom.IElement> AtLeastOne(IReadOnlyList<AngleSharp.Dom.IElement> elements)
    {
        Assert.NotEmpty(elements);
        return elements;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    /// <summary>
    /// The fix that closes the window rather than reporting it afterwards: while a
    /// run is in flight there is no schedule a question could honestly be answered
    /// about, so nothing in the conversation is operable.
    /// </summary>
    [Fact]
    public async Task Nothing_in_the_conversation_can_be_asked_while_a_run_is_in_flight()
    {
        var scheduler = Arrange();
        var cut = Render<SchedulePage>();
        cut.WaitForChatReady();

        scheduler.UseGate = true;
        await cut.ActAsync("#sched-generate", element => element.Click());

        cut.WaitForAssertion(() => Assert.All(
            cut.FindAll(".chat-suggestions .chip"),
            chip => Assert.True(chip.HasAttribute("disabled"))));
        Assert.True(cut.Find("#chat-question").HasAttribute("disabled"));

        // And the handler refuses it too. bUnit raises the event whether or not
        // the element is disabled, which makes the browser's own race — a key
        // pressed against a render that has not caught up — reproducible here.
        await cut.ActAsync(".chat-suggestions .chip", element => element.Click());
        Assert.Empty(cut.FindAll(".chat-turn"));
        Assert.Empty(cut.FindAll(".chat-busy"));

        scheduler.Gate.SetResult();

        cut.WaitForAssertion(() => Assert.All(
            cut.FindAll(".chat-suggestions .chip"),
            chip => Assert.False(chip.HasAttribute("disabled"))));

        // The refused question left nothing behind: no turn, and no note about
        // one having been dropped, because none was ever accepted.
        Assert.Empty(cut.FindAll(".chat-turn"));
        Assert.Empty(cut.FindAll(".chat-note[role=status]"));
    }

    /// <summary>
    /// And the honest half: if a question is superseded anyway, the page says so
    /// instead of dropping it. The distinction is which cancellation it was — the
    /// page's own lifetime, or the conversation moving to another schedule.
    /// </summary>
    /// <remarks>
    /// The question is held open at the transport, because the on-device answerer
    /// returns before a reset could ever overtake it. That is also why this was
    /// only ever seen under load.
    /// </remarks>
    [Fact]
    public async Task A_superseded_question_is_reported_rather_than_dropped()
    {
        var transport = new GatedHttpMessageHandler(
            """{"choices":[{"message":{"content":"CNC-200 is the bottleneck."}}]}""");
        var scheduler = Arrange(new HttpClient(transport), Hostile.Configured(AssistantProvider.OpenAiCompatible));

        var cut = Render<SchedulePage>();
        cut.WaitForChatReady();

        await cut.ActAsync(".chat-suggestions .chip", element => element.Click());
        await transport.Started;

        // The planner presses Generate while the model is still answering.
        scheduler.Result = Sample.OnTime() with { Horizon = new DateTime(2026, 6, 8, 6, 0, 0) };
        await cut.ActAsync("#sched-generate", element => element.Click());
        transport.Release();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".chat-note[role=status]")));
        Assert.Equal("Chat_SupersededByNewRun", cut.Find(".chat-note[role=status]").TextContent);

        // Nothing was appended, and it is reported as an outcome rather than a failure.
        Assert.Empty(cut.FindAll(".chat-turn"));
        Assert.Empty(cut.FindAll(".chat .form-banner.error"));
    }
}
