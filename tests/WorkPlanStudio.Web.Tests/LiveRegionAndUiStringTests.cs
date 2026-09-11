using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.WorkingTime;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Two defects with the same shape: something changes on screen and nothing says so.
/// A live region has to exist in the DOM <em>before</em> its content changes — one
/// inserted already populated is announced by no screen reader — and a string that
/// never reached <c>IStringLocalizer</c> is announced in the wrong language.
/// </summary>
public sealed class LiveRegionAndUiStringTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private FakeScheduleService Arrange(ScheduleResult result)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var fake = new FakeScheduleService { Result = result };
        Services.AddSingleton<IProductionScheduleService>(fake);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton<RuleBasedNarrator>();
        Services.AddSingleton<IAssistantConfig>(new FakeAssistantConfig());
        Services.AddSingleton(new HttpClient());
        Services.AddSingleton<ScheduleAssistant>();
        Services.AddSingleton<OfflineScheduleAnswerer>();
        Services.AddSingleton<ScheduleChat>();
        Services.AddScheduleExport();
        Services.AddOptimalityProver();
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("live-region.db", new FakeStorage())));
        return fake;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    /// <summary>
    /// The thread was rendered only once there were turns to put in it, which is why
    /// the first answer of every conversation was silent and the second one was not.
    /// </summary>
    [Fact]
    public void The_chat_thread_is_a_live_region_before_the_first_question_is_asked()
    {
        Arrange(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".chat-thread")));

        var thread = cut.Find(".chat-thread");
        Assert.Equal("polite", thread.GetAttribute("aria-live"));
        Assert.Equal("additions", thread.GetAttribute("aria-relevant"));
        Assert.Empty(cut.FindAll(".chat-turn"));

        cut.Find(".chat-suggestions .chip").Click();

        // The same element, now with content — not a new element carrying content.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".chat-turn").Count));
        Assert.Equal("polite", cut.Find(".chat-thread").GetAttribute("aria-live"));
    }

    /// <summary>
    /// Regenerating replaces the KPI cards, the chart, the jobs table and the
    /// narration. The spinner cannot cover it: it is gated on there being no result
    /// yet, and a re-run keeps the previous one on screen throughout.
    /// </summary>
    [Fact]
    public void Generating_a_schedule_announces_its_headline()
    {
        Arrange(Sample.WithLateJob());

        var cut = Render<SchedulePage>();

        // The region exists from the first paint, empty, and is filled once a run
        // completes — the order that makes an announcement audible.
        var region = cut.Find(".sched-results > .sr-only[role=status]");
        Assert.Equal("polite", region.GetAttribute("aria-live"));
        cut.WaitForAssertion(() => Assert.Contains(
            "Ui_ScheduleAnnounced",
            cut.Find(".sched-results > .sr-only[role=status]").TextContent,
            StringComparison.Ordinal));
    }

    [Fact]
    public void The_results_are_marked_busy_only_while_a_run_is_in_flight()
    {
        Arrange(Sample.OnTime());

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find(".sched-results").GetAttribute("aria-busy")));
    }

    /// <summary>
    /// The sweep that found <c>aria-label="Menu"</c> — the app's only untranslated
    /// string, which the mobile E2E test then located by that English name and so
    /// pinned in place.
    /// </summary>
    [Fact]
    public void No_component_hard_codes_an_aria_label_title_or_placeholder()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, number) => (File: Path.GetFileName(file), Number: number + 1, Line: line)))
            .SelectMany(entry => Regex
                .Matches(entry.Line, @"(?:aria-label|placeholder|alt)=""(?<value>[^""@][^""]*)""")
                .Select(match => $"{entry.File}:{entry.Number} {match.Value}"))
            .ToArray();

        Assert.True(offenders.Length == 0, "literal user-facing text in markup: " + string.Join(" | ", offenders));
    }

    /// <summary>Every key this stream added exists in both languages, with the agreed prefix.</summary>
    [Fact]
    public void The_ui_resource_keys_are_defined_in_both_languages()
    {
        var english = Keys("SharedResource.resx");
        var german = Keys("SharedResource.de.resx");

        var ui = english.Where(key => key.StartsWith("Ui_", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(ui);
        Assert.All(ui, key => Assert.Contains(key, german));
    }

    private static HashSet<string> Keys(string fileName) =>
        XDocument
            .Load(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Resources", fileName))
            .Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// <c>role="img"</c> tells assistive technology to treat the whole subtree as one
    /// picture and ignore what is inside it. The weekly preview used it with the alt
    /// text "Weekly pattern" — the name of the widget, not its content — which threw
    /// away all seven days.
    /// </summary>
    [Fact]
    public void The_weekly_preview_is_readable_as_text_rather_than_alt_texted_with_its_own_name()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var rules = new PlantSettings().ToRules();
        var weekStart = new DateTime(2026, 1, 5);
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.OneShift, rules, [], weekStart, weekStart.AddDays(7));

        var cut = Render<WeekStrip>(parameters => parameters.Add(component => component.Timeline, timeline));

        Assert.Empty(cut.FindAll("[role=img]"));
        Assert.Equal("true", cut.Find(".week-strip").GetAttribute("aria-hidden"));

        var rows = cut.FindAll(".week-table tbody tr");
        Assert.Equal(7, rows.Count);
        Assert.All(rows, row => Assert.Equal("row", row.QuerySelector("th")!.GetAttribute("scope")));
        // The working window of a weekday, in words — the thing role="img" discarded.
        Assert.Contains(rows, row => row.TextContent.Contains("Segment_Working", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.TextContent.Contains("07:00", StringComparison.Ordinal));
    }
}
