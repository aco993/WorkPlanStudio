using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// Page object for <c>/schedule</c> — keeps selectors and actions in one place.
///
/// Controls are addressed the way a user names them: by role and accessible
/// name, or by the label text of the field. The page previously used positional
/// CSS (<c>label:nth-of-type(3)</c>, <c>.stat-value</c> first) which silently
/// retargeted whenever the markup was reordered — including one case where the
/// third field is a different parameter depending on the selected rule. A label
/// lookup either finds the field the test is about or fails loudly, which is the
/// point.
///
/// Where an element genuinely has no role and no name — a Gantt bar, the "late"
/// pill — the class is the only handle the markup offers and is used as such;
/// those classes are the visual encoding the assertion is about, not a position.
/// </summary>
public sealed class SchedulePage
{
    /// <summary>
    /// The user-visible names this page object addresses controls by. They are
    /// culture-dependent, so switching the UI language switches the set rather
    /// than leaving the page object silently pointing at English names.
    /// </summary>
    public sealed record Vocabulary(
        string Generate,
        string DispatchRule,
        string DueRule,
        string FlowFactor,
        string MultiStart,
        string LocalSearch,
        string Seed,
        string Makespan)
    {
        public static Vocabulary English { get; } = new(
            "Generate schedule", "Dispatch rule", "Target-date rule", "Flow factor",
            "Multi-start runs", "Local-search steps", "Seed", "Makespan");

        public static Vocabulary German { get; } = new(
            "Plan erzeugen", "Prioritätsregel", "Zieltermin-Regel", "Durchlauffaktor",
            "Multi-Start-Läufe", "Lokale-Suche-Schritte", "Startwert", "Durchlaufzeit");
    }

    private readonly IPage _page;
    private readonly string _baseUrl;
    private Vocabulary _words = Vocabulary.English;

    public SchedulePage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>The underlying page, for checks the page object does not wrap.</summary>
    public IPage Page => _page;

    public ILocator Heading => _page.GetByRole(AriaRole.Heading, new() { Level = 1 });
    public ILocator KpiCards => _page.Locator(".stat-card");
    public ILocator GanttBars => _page.Locator(".gantt-bar");
    public ILocator LateBars => _page.Locator(".gantt-bar.late");
    public ILocator LatePills => _page.Locator(".pill.late");
    public ILocator JobRows => _page.Locator(".data-table tbody tr");

    /// <summary>The Generate button, which renames itself while the engine runs.</summary>
    private ILocator GenerateButton =>
        _page.GetByRole(AriaRole.Button, new() { Name = _words.Generate, Exact = true });

    public async Task GotoAsync()
    {
        await AppReady.GotoAsync(_page, $"{_baseUrl}/schedule");
        // wait for the WASM app to boot and the first schedule to render
        await _page.WaitForSelectorAsync(".gantt, .empty-state", new() { Timeout = AppReady.BootTimeoutMilliseconds });
    }

    public Task SetDispatchRuleAsync(string enumName) =>
        Field(_words.DispatchRule).SelectOptionAsync(new SelectOptionValue { Value = enumName });

    public Task SetDueRuleAsync(string enumName) =>
        Field(_words.DueRule).SelectOptionAsync(new SelectOptionValue { Value = enumName });

    /// <summary>
    /// The flow factor belongs to the total-work-content target rule and is the
    /// only parameter rendered in that slot for that rule; under any other rule
    /// this throws instead of writing the value into whichever field happens to
    /// sit there.
    /// </summary>
    public Task SetFlowFactorAsync(string value) => Field(_words.FlowFactor).FillAsync(value);

    public Task SetMultiStartAsync(string value) => Field(_words.MultiStart).FillAsync(value);

    public Task SetLocalSearchStepsAsync(string value) => Field(_words.LocalSearch).FillAsync(value);

    public Task SetSeedAsync(string value) => Field(_words.Seed).FillAsync(value);

    /// <summary>Turns the optimiser off so a dispatch rule's raw effect is visible.</summary>
    public async Task UseRuleOnlyAsync()
    {
        await SetMultiStartAsync("1");
        await SetLocalSearchStepsAsync("0");
    }

    public async Task GenerateAsync()
    {
        await GenerateButton.ClickAsync();

        // While the engine runs the button is disabled and renames itself to
        // "Scheduling…", so the original name coming back enabled is the app's
        // own "finished" signal — a condition, not a sleep, and unique on the
        // page, so it cannot silently resolve to the chat's Send button.
        await Assertions.Expect(GenerateButton).ToBeEnabledAsync(new() { Timeout = 30_000 });
    }

    public async Task SwitchToGermanAsync()
    {
        await _page.GetByRole(AriaRole.Button, new() { Name = "DE", Exact = true }).ClickAsync();
        await _page.WaitForSelectorAsync(".gantt, .empty-state", new() { Timeout = AppReady.BootTimeoutMilliseconds });
        _words = Vocabulary.German;
    }

    /// <summary>
    /// The makespan KPI, found by the label printed on its card rather than by
    /// being first, so reordering the KPI row cannot silently repoint every
    /// determinism assertion at a different number.
    /// </summary>
    public Task<string> MakespanTextAsync() =>
        _page.Locator(".stat-card")
            .Filter(new LocatorFilterOptions { HasText = _words.Makespan })
            .Locator(".stat-value")
            .InnerTextAsync();

    public async Task<string> DocumentLanguageAsync() =>
        await _page.Locator("html").GetAttributeAsync("lang") ?? "";

    public async Task ScreenshotAsync(string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("E2E_ARTIFACTS") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        await _page.ScreenshotAsync(new() { Path = Path.Combine(dir, fileName), FullPage = true });
    }

    /// <summary>
    /// The control of the parameter field whose label starts with the given
    /// text. Not exact: one label carries its unit in brackets
    /// ("Flow factor (× work)") and the unit is not what the test is naming.
    /// </summary>
    private ILocator Field(string label) => _page.GetByLabel(label, new() { Exact = false });
}
