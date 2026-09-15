using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Chat;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The half of the API-key fix that lives on the page. The store stopped putting
/// the key in the settings blob and started keeping it per provider — but the
/// settings dialog was still reading it back into a live <c>value</c> attribute
/// every time it opened, which put the secret in the DOM, where any script on
/// this origin can read it, and where it lands in a screenshot or a DOM dump.
/// <para>
/// A blank field means "keep the stored key", so the page can change the
/// endpoint or the model without ever holding the secret. These tests hold that
/// line: the key must not appear anywhere in the rendered markup, the user must
/// be told whether one is stored, and they must be able to remove it.
/// </para>
/// </summary>
public sealed class AssistantKeyInTheDomTests : AppBunitContext
{
    private const string Secret = "sk-do-not-render-me-0123456789";

    private readonly TempDatabaseFiles _files = new();
    private readonly FakeAssistantConfig _config = new();

    private void Arrange()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProductionScheduleService>(new FakeScheduleService { Result = Sample.OnTime() });
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton<RuleBasedNarrator>();
        Services.AddSingleton<IAssistantConfig>(_config);
        Services.AddSingleton(new HttpClient());
        Services.AddSingleton<ScheduleAssistant>();
        Services.AddSingleton<OfflineScheduleAnswerer>();
        Services.AddSingleton<ScheduleChat>();
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("assistant-dom.db", new FakeStorage())));

        // The schedule page renders the export menu, which has its own dependencies.
        Services.AddScheduleExport();
        Services.AddOptimalityProver();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private async Task<IRenderedComponent<SchedulePage>> OpenSettingsAsync()
    {
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".assistant-card")));
        await cut.ActAsync(".assistant-card .icon-btn", settings => settings.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        return cut;
    }

    [Fact]
    public async Task A_stored_key_never_reaches_the_markup()
    {
        Arrange();
        _config.Settings = _config.Settings with { Enabled = true, ApiKey = Secret };

        var cut = await OpenSettingsAsync();

        Assert.DoesNotContain(Secret, cut.Markup, StringComparison.Ordinal);
        var field = cut.FindAll(".modal-card input[type=password]").Single();
        Assert.Equal("", field.GetAttribute("value") ?? "");
    }

    /// <summary>
    /// Hiding the key must not hide its existence: a blank field with no
    /// explanation reads as "no key configured" and invites the user to paste it
    /// again, which is the opposite of the point.
    /// </summary>
    [Fact]
    public async Task The_field_says_whether_a_key_is_already_stored()
    {
        Arrange();
        _config.Settings = _config.Settings with { Enabled = true, ApiKey = Secret };

        var stored = (await OpenSettingsAsync()).FindAll(".modal-card input[type=password]").Single();
        Assert.Equal("Ai_KeyStored", stored.GetAttribute("placeholder"));
    }

    [Fact]
    public async Task With_no_key_stored_the_field_says_so_and_offers_no_way_to_forget_one()
    {
        Arrange();
        _config.Settings = _config.Settings with { Enabled = true, ApiKey = "" };

        var cut = await OpenSettingsAsync();

        Assert.Equal("Ai_KeyNone", cut.FindAll(".modal-card input[type=password]").Single().GetAttribute("placeholder"));
        Assert.DoesNotContain(cut.FindAll(".modal-card button"), b => b.TextContent.Contains("Ai_KeyForget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Forgetting_the_key_clears_the_store_and_says_it_did()
    {
        Arrange();
        _config.Settings = _config.Settings with { Enabled = true, ApiKey = Secret };

        var cut = await OpenSettingsAsync();
        await cut.InvokeAsync(() =>
            cut.FindAll(".modal-card button").Single(b => b.TextContent.Contains("Ai_KeyForget", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => Assert.Contains("Ai_KeyForgotten", cut.Markup, StringComparison.Ordinal));
        Assert.False(_config.Settings.HasApiKey);
        Assert.DoesNotContain(Secret, cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>The risk the app cannot engineer away is stated where the key is entered.</summary>
    [Fact]
    public async Task The_dialog_states_the_residual_risk()
    {
        Arrange();
        Assert.Contains("Ai_KeyRisk", (await OpenSettingsAsync()).Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once. Measured on the published v0.4.0 the dialog carried two paragraphs
    /// that both said browser storage is not a vault and not to use a shared
    /// computer, in different words - so the reader looks for the difference
    /// between them and there is none. The CORS note stays: it says something
    /// else.
    /// </summary>
    [Fact]
    public async Task The_risk_is_stated_once()
    {
        Arrange();
        var cut = await OpenSettingsAsync();

        var said = cut.FindAll(".modal-card .assistant-privacy")
            .Select(p => p.TextContent.Trim()).ToList();

        Assert.Equal(said.Count, said.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("Sched_Ai_Privacy", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>One paragraph names the risk; the others must not name it again.</summary>
    [Theory]
    [InlineData("SharedResource.resx", "vault")]
    [InlineData("SharedResource.de.resx", "Tresor")]
    public void Only_one_of_the_dialog_texts_calls_the_browser_no_vault(string file, string word)
    {
        var resource = File.ReadAllText(
            Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Resources", file));

        var mentions = System.Text.RegularExpressions.Regex
            .Matches(resource, $"<data name=\"(?<k>Ai_[^\"]+|Sched_Ai_[^\"]+)\"[^>]*><value>(?<v>.*?)</value>",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Where(m => m.Groups["v"].Value.Contains(word, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["k"].Value)
            .ToList();

        Assert.Single(mentions);
    }
}
