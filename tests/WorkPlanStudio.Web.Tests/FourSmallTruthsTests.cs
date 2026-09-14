using Bunit;
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
/// Four small places where the app knew something and said something else — all
/// found by driving the published site rather than by reading the code.
/// </summary>
public sealed class FourSmallTruthsTests : AppBunitContext
{
    private BrowserDatabase Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("truths.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlanEditor>>(NullLogger<WorkPlanStudio.Pages.WorkPlanEditor>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlans>>(NullLogger<WorkPlanStudio.Pages.WorkPlans>.Instance);
        return database;
    }

    // ----- 1. a work plan that is not there says so, instead of offering a blank form -----

    [Fact]
    public async Task A_work_plan_that_does_not_exist_says_so_instead_of_opening_an_empty_editor()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, 9999));

        cut.WaitForAssertion(() => Assert.Contains("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal));

        // No form to fill in, and no Save to press: the page used to hand back an
        // empty editor and admit the plan was gone only after the save failed.
        Assert.Empty(cut.FindAll("#wp-number"));
        Assert.DoesNotContain("Common_Save", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("a[href=\"work-plans\"]"));
    }

    [Fact]
    public async Task A_work_plan_that_does_exist_still_opens_its_editor()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var plan = (await new WorkPlanService(database).GetAllAsync(cancellationToken)).First();

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, plan.Id));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wp-number")));
        Assert.DoesNotContain("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal);
    }

    // ----- 2. the refusal is stated before the click, not after the confirmation -----

    [Fact]
    public async Task A_plan_an_order_was_raised_from_cannot_be_deleted_and_says_why_up_front()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkPlans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var used = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("WP-1001", StringComparison.Ordinal));
        var delete = used.QuerySelectorAll("button.icon-btn").Last();

        Assert.True(delete.HasAttribute("disabled"));
        Assert.Equal("WorkPlans_InUse", delete.GetAttribute("title"));
    }

    [Fact]
    public async Task A_plan_nothing_was_ordered_from_keeps_its_delete_button()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var plans = new WorkPlanService(database);
        var free = (await plans.GetAllAsync(cancellationToken)).First(p => p.PlanNumber == "WP-1004");   // archived, never ordered
        var counts = await plans.GetOrderCountsAsync(cancellationToken);
        Assert.False(counts.ContainsKey(free.Id));

        var cut = Render<WorkPlanStudio.Pages.WorkPlans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("WP-1004", StringComparison.Ordinal));
        var delete = row.QuerySelectorAll("button.icon-btn").Last();

        Assert.False(delete.HasAttribute("disabled"));
        Assert.Equal("Common_Delete", delete.GetAttribute("title"));
    }

    // ----- 3. "no room left" is not the same sentence as "something went wrong" -----

    [Fact]
    public async Task A_full_browser_says_so_rather_than_that_the_action_failed()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("full.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var centers = new CostCenterService(database);
        storage.QuotaExceeded = true;
        var result = await centers.SaveAsync(new WorkPlanStudio.Models.CostCenter { Code = "CC-FULL", Name = "No room" }, cancellationToken);
        storage.QuotaExceeded = false;

        Assert.Equal(ApplicationResultStatus.StorageFull, result.Status);

        // And the form turns that status into the sentence that names the cause.
        var errors = new WorkPlanStudio.Services.Forms.FormErrorState();
        errors.Apply(result, new PassThroughLocalizer<SharedResource>());
        Assert.Equal("Error_StorageFull", errors.Summary);
    }

    [Fact]
    public async Task A_storage_failure_that_is_not_about_room_keeps_the_general_sentence()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("broken.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var centers = new CostCenterService(database);
        storage.ThrowOnSave = true;
        var result = await centers.SaveAsync(new WorkPlanStudio.Models.CostCenter { Code = "CC-FAIL", Name = "Broken" }, cancellationToken);
        storage.ThrowOnSave = false;

        Assert.Equal(ApplicationResultStatus.PersistenceFailed, result.Status);
    }

    // ----- 4. a file has nothing to select from -----

    [Fact]
    public void The_import_names_the_plan_number_instead_of_telling_a_file_to_pick_one()
    {
        // "Select a work plan." is right beside a dropdown and wrong beside a CSV
        // row, which has a number in it that nobody recognised.
        var resource = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "WorkPlanStudio", "Resources", "SharedResource.resx"));

        Assert.Contains("Imp_WorkPlanNotFound", resource, StringComparison.Ordinal);
        Assert.Contains("{0}", Value(resource, "Imp_WorkPlanNotFound"), StringComparison.Ordinal);
        Assert.DoesNotContain("Select", Value(resource, "Imp_WorkPlanNotFound"), StringComparison.OrdinalIgnoreCase);

        // The form's own message is untouched: there, selecting is exactly it.
        Assert.Contains("Select", Value(resource, "Val_WorkPlanMissing"), StringComparison.OrdinalIgnoreCase);
    }

    private static string Value(string resx, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            resx, $"<data name=\"{key}\"[^>]*><value>(?<v>.*?)</value>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, $"{key} is not in the resource file");
        return match.Groups["v"].Value;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
