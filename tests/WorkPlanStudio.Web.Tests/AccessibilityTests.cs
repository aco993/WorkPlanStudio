using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;

namespace WorkPlanStudio.Web.Tests;

public sealed class AccessibilityTests : BunitContext
{
    [Fact]
    public void Modal_has_dialog_semantics_localized_close_name_and_escape_behavior()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var closed = false;

        var cut = Render<Modal>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.Title, "Accessible title")
            .Add(component => component.VisibleChanged,
                EventCallback.Factory.Create<bool>(this, visible => closed = !visible)));

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        var titleId = dialog.GetAttribute("aria-labelledby");
        Assert.Equal("Accessible title", cut.Find($"#{titleId}").TextContent);
        Assert.Equal("Common_Close", cut.Find(".modal-head button").GetAttribute("aria-label"));

        cut.Find(".modal-backdrop-custom").KeyDown("Escape");

        Assert.True(closed);
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "workplanModal.open");
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "workplanModal.close");
    }

    /// <summary>
    /// Renders the real pages against a real SQLite database. Icon-only controls,
    /// data tables and busy states are the three things that most often ship
    /// without an accessible name, so they are asserted rather than eyeballed.
    /// </summary>
    private BrowserDatabase ArrangePages(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("a11y.db", new FakeStorage());

        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddSingleton(new WorkCenterService(database));
        Services.AddSingleton(new WorkPlanService(database));
        return database;
    }

    [Fact]
    public async Task Data_table_headers_declare_their_scope_and_the_table_is_named()
    {
        using var files = new TempDatabaseFiles();
        var database = ArrangePages(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("thead th")));

        // Without scope, a screen reader has to guess whether a th labels a row
        // or a column once the table has more than one header row.
        Assert.All(cut.FindAll("thead th"), header => Assert.Equal("col", header.GetAttribute("scope")));
        Assert.NotEmpty(cut.FindAll("caption"));
    }

    [Fact]
    public async Task Icon_only_controls_carry_an_accessible_name()
    {
        using var files = new TempDatabaseFiles();
        var database = ArrangePages(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.icon-btn")));

        // An icon button has no text node, so without aria-label a screen reader
        // announces "button" and nothing else.
        Assert.All(cut.FindAll("button.icon-btn"), button =>
            Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label"))));
    }

    [Fact]
    public async Task Validation_errors_are_tied_to_the_field_they_describe()
    {
        using var files = new TempDatabaseFiles();
        var database = ArrangePages(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".page-head .btn-primary")));

        cut.Find(".page-head .btn-primary").Click();                       // open the editor
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        cut.FindAll(".modal-foot .btn").First(b => !b.ClassList.Contains("btn-ghost")).Click();   // save it empty

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".field-error")));

        var invalid = cut.FindAll("input[aria-invalid=true]");
        Assert.NotEmpty(invalid);
        foreach (var describedBy in invalid.Select(input => input.GetAttribute("aria-describedby")))
        {
            Assert.False(string.IsNullOrWhiteSpace(describedBy), "an invalid input must point at its message");
            Assert.NotEmpty(cut.FindAll($"#{describedBy}"));   // and the message must exist
        }
    }

    /// <summary>
    /// Checked over the sources rather than a render: whether a page happens to
    /// still be loading when a test looks at it is a race, but "every spinner we
    /// ship announces itself" is a property of the markup.
    /// </summary>
    [Fact]
    public void Every_busy_indicator_is_exposed_as_a_status()
    {
        var componentsRoot = Path.Join(RepoFiles.Root, "src", "WorkPlanStudio");

        // A bare spinning div is invisible to a screen reader: it needs a status
        // role, hidden decoration, and text to announce.
        var offenders = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file).Select(line => (File: file, Line: line)))
            .Where(entry => entry.Line.Contains("loading-row", StringComparison.Ordinal))
            .Where(entry => !entry.Line.Contains("role=\"status\"", StringComparison.Ordinal)
                         || !entry.Line.Contains("aria-hidden=\"true\"", StringComparison.Ordinal)
                         || !entry.Line.Contains("sr-only", StringComparison.Ordinal))
            .Select(entry => $"{Path.GetFileName(entry.File)}: {entry.Line.Trim()}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "busy indicators without a status role: " + string.Join(" | ", offenders));
    }
}
