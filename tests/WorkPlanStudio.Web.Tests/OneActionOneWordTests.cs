using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Three smaller findings from the published 0.3.9, all of the same kind: the
/// application knew something and the screen said something else.
/// <list type="bullet">
/// <item>The row button said <em>Cancel order</em>, the dialog it opened was
/// titled <em>Withdraw order</em>, and the status it produced was
/// <em>Cancelled</em> — one action, two verbs, in both languages.</item>
/// <item>The import's <em>Check the file</em> button was disabled until every
/// required column was matched, and said so only up the page beside the
/// column.</item>
/// <item>The weekly-pattern card read as a setting; only its <c>sr-only</c>
/// labels said "preview".</item>
/// </list>
/// </summary>
public sealed class OneActionOneWordTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private static string Resource(string file) =>
        File.ReadAllText(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Resources", file));

    private static string Value(string resx, string key)
    {
        var match = Regex.Match(
            resx, $"<data name=\"{key}\"[^>]*><value>(?<v>.*?)</value>", RegexOptions.Singleline);
        Assert.True(match.Success, $"{key} is not in the resource file");
        return match.Groups["v"].Value;
    }

    [Theory]
    [InlineData("SharedResource.resx")]
    [InlineData("SharedResource.de.resx")]
    public void The_dialog_uses_the_same_verb_as_the_button_that_opens_it(string file)
    {
        var resource = Resource(file);

        // The confirm dialog's title is the action, so it is the button's own words.
        Assert.Equal(Value(resource, "Orders_Cancel"), Value(resource, "Confirm_CancelOrderTitle"));
    }

    [Theory]
    [InlineData("SharedResource.resx", "withdraw")]
    [InlineData("SharedResource.de.resx", "zurückziehen")]
    public void The_second_verb_is_gone_from_the_body_too(string file, string abandoned)
    {
        var body = Value(Resource(file), "Confirm_CancelOrderBody");

        Assert.DoesNotContain(abandoned, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_disabled_dry_run_button_carries_its_own_reason()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = _files.CreateDatabase("import.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.DataImport>>(
            NullLogger<WorkPlanStudio.Pages.DataImport>.Instance);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var page = File.ReadAllText(
            Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Pages", "DataImport.razor"));

        // The button points at a sentence, and the sentence is rendered next to it
        // rather than only beside the column that is missing.
        Assert.Contains("aria-describedby=\"@(blocked ? \"import-dry-run-blocked\" : null)\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"import-dry-run-blocked\"", page, StringComparison.Ordinal);
        Assert.NotEmpty(Value(Resource("SharedResource.resx"), "Import_DryRunBlocked"));
        Assert.NotEmpty(Value(Resource("SharedResource.de.resx"), "Import_DryRunBlocked"));
    }

    [Theory]
    [InlineData("SharedResource.resx", "preview")]
    [InlineData("SharedResource.de.resx", "vorschau")]
    public void The_preview_card_says_so_where_a_sighted_reader_can_see_it(string file, string word)
    {
        var resource = Resource(file);

        Assert.Contains(word, Value(resource, "WorkingTime_Preview"), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(Value(resource, "WorkingTime_PreviewNote"));
    }
}
