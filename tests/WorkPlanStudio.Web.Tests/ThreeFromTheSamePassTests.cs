using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The three findings from the pass after 0.3.4 that were not about the editor
/// forgetting which plan it was showing: a language switch that persisted a
/// choice the reader then cancelled, and a form that reported four problems while
/// marking one.
/// </summary>
public sealed class ThreeFromTheSamePassTests : AppBunitContext
{
    // ----- 1. the language is asked for in the address, not written before the question -----

    [Fact]
    public void Switching_the_language_writes_nothing_and_asks_in_the_address()
    {
        // Strict is the assertion: the switch used to persist the choice through
        // JS before a reload that the editor could still refuse, which left the
        // app disagreeing with itself and changed the language on a later visit
        // the reader had cancelled. Any interop call here fails this test.
        JSInterop.Mode = JSRuntimeMode.Strict;
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());

        var navigation = Services.GetRequiredService<NavigationManager>();

        // Pinned rather than inferred: a switch to the language already running
        // does nothing by design, and the host's own culture is not the same on
        // every machine - this developer's Windows is German and the CI runner is
        // invariant, which is how the first version of this test failed only there.
        var host = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var cut = Render<WorkPlanStudio.Components.CultureSelector>();

            cut.FindAll("button.culture-btn").Single(b => b.TextContent.Trim() == "DE").Click();
        }
        finally
        {
            CultureInfo.CurrentCulture = host;
        }

        Assert.Contains("culture=de-DE", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_that_starts_in_a_language_is_the_one_that_remembers_it()
    {
        // The other half lives in app.js, before the runtime exists: a reload that
        // never happened must leave nothing behind, so nothing may write the
        // preference except a page that really did start with it in its address.
        var script = File.ReadAllText(Path.Join(RepoFiles.AppWwwroot, "js", "app.js"));

        Assert.Contains("rememberTheLanguageThisPageStartedIn", script, StringComparison.Ordinal);
        Assert.Contains("searchParams.get('culture')", script, StringComparison.Ordinal);

        // And the write-before-asking door is gone rather than merely unused: the
        // remembered language is readable from the app and writable only here.
        var accessor = script[script.IndexOf("window.blazorCulture", StringComparison.Ordinal)..];
        accessor = accessor[..accessor.IndexOf("};", StringComparison.Ordinal)];
        Assert.DoesNotContain("set", accessor, StringComparison.Ordinal);
    }

    // ----- 2 and 3. every problem is marked where it is, and named in the summary -----

    private BrowserDatabase Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("marked.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlanEditor>>(NullLogger<WorkPlanStudio.Pages.WorkPlanEditor>.Instance);
        return database;
    }

    /// <summary>Opens the first plan and empties one operation's description.</summary>
    private async Task<IRenderedComponent<WorkPlanStudio.Pages.WorkPlanEditor>> ABadCellAsync(TempDatabaseFiles files)
    {
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var first = (await new WorkPlanService(database).GetAllAsync(Xunit.TestContext.Current.CancellationToken)).First();

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, first.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        await cut.ActAsync("tbody tr input[maxlength=\"120\"]", description => description.Input(""));
        await cut.ActAsync(".page-head .btn-primary", save => save.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=alert]")));
        return cut;
    }

    [Fact]
    public async Task The_cell_that_is_wrong_is_the_cell_that_is_marked()
    {
        using var files = new TempDatabaseFiles();
        var cut = await ABadCellAsync(files);

        var row = cut.Find("tbody tr");
        var description = row.QuerySelector("input[maxlength=\"120\"]")!;

        Assert.Equal("true", description.GetAttribute("aria-invalid"));
        Assert.Contains("error", description.ClassName ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cells_that_are_fine_are_left_alone()
    {
        // A marker that marks everything is the same as no marker at all.
        using var files = new TempDatabaseFiles();
        var cut = await ABadCellAsync(files);

        var row = cut.Find("tbody tr");
        var remarks = row.QuerySelector("input[maxlength=\"250\"]")!;
        var setup = row.QuerySelectorAll("input[type=number]").Last();

        Assert.Equal("false", remarks.GetAttribute("aria-invalid"));
        Assert.Equal("false", setup.GetAttribute("aria-invalid"));
    }

    [Fact]
    public async Task Two_operations_with_one_number_are_both_marked()
    {
        // Measured, and kept on purpose. The validator names the row by its
        // operation number, so when the reader has two operations numbered 10 it
        // cannot say which of them it means - and neither can this page. Marking
        // both is the truthful answer to an ambiguous question; the first line of
        // the summary is the duplicate number itself.
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var first = (await new WorkPlanService(database).GetAllAsync(Xunit.TestContext.Current.CancellationToken)).First();

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, first.Id));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("tbody tr").Count > 1));

        var firstNumber = cut.FindAll("tbody tr input[type=number]")[0].GetAttribute("value");
        await cut.ActAsync(
            "tbody tr:nth-child(2) input[type=number]",
            number => number.Input(firstNumber ?? "10"));
        await cut.ActAsync(".page-head .btn-primary", save => save.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=alert]")));

        var numbers = cut.FindAll("tbody tr td.narrow input");
        Assert.All(numbers.Take(2), input => Assert.Equal("true", input.GetAttribute("aria-invalid")));
    }

    [Fact]
    public async Task The_summary_line_names_the_operation_and_the_column()
    {
        using var files = new TempDatabaseFiles();
        var cut = await ABadCellAsync(files);

        var summary = cut.Find("[role=alert]").TextContent;

        // The bare word was the whole finding: three full sentences and then
        // "Required", with no field and no row to look in.
        Assert.Contains("Val_InOperation", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Val_Required", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void That_sentence_really_does_carry_the_row_the_column_and_the_reason()
    {
        // The test localizer hands back the key, so the render above can only show
        // that the wrapper was used. What the wrapper says is in the resources, in
        // both languages, and all three parts have to survive translation.
        foreach (var file in new[] { "SharedResource.resx", "SharedResource.de.resx" })
        {
            var resource = File.ReadAllText(Path.Join(
                RepoFiles.Root, "src", "WorkPlanStudio", "Resources", file));
            var sentence = Value(resource, "Val_InOperation");

            Assert.Contains("{0}", sentence, StringComparison.Ordinal);   // which operation
            Assert.Contains("{1}", sentence, StringComparison.Ordinal);   // which column
            Assert.Contains("{2}", sentence, StringComparison.Ordinal);   // and what is wrong
        }
    }

    private static string Value(string resx, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            resx, $"<data name=\"{key}\"[^>]*><value>(?<v>.*?)</value>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, $"{key} is not in {resx.Length} characters of resource file");
        return match.Groups["v"].Value;
    }
}
