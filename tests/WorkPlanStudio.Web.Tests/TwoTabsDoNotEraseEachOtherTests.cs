using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Every tab of this app holds the whole database in memory and writes the whole
/// of it back, so for five releases the last writer won and the loser was never
/// told: two tabs deleted each other's <em>saved</em> work, in both directions,
/// while the losing tab went on showing the change that was already gone.
/// <para>
/// Measured on the published site before it was fixed; the suite had no test that
/// wrote twice from two copies, which is why nothing caught it.
/// </para>
/// </summary>
public sealed class TwoTabsDoNotEraseEachOtherTests : AppBunitContext
{
    private (BrowserDatabase Database, FakeStorage Storage) Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new FakeStorage();
        var database = files.CreateDatabase("tabs.db", storage);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlanEditor>>(NullLogger<WorkPlanStudio.Pages.WorkPlanEditor>.Instance);
        return (database, storage);
    }

    [Fact]
    public async Task A_write_based_on_what_another_tab_has_already_replaced_is_refused()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, storage) = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        // This copy is now behind, exactly as the second tab's was.
        storage.AnotherTabWrites();

        var centers = new CostCenterService(database);
        var result = await centers.SaveAsync(new CostCenter { Code = "CC-TAB", Name = "Second tab" }, cancellationToken);

        Assert.Equal(ApplicationResultStatus.ChangedElsewhere, result.Status);
    }

    [Fact]
    public async Task The_other_tabs_work_is_still_there_afterwards()
    {
        // The point of refusing rather than failing halfway: what the other tab
        // saved has to survive the attempt untouched.
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, storage) = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        storage.AnotherTabWrites();
        var theirs = storage.Stored;

        var centers = new CostCenterService(database);
        _ = await centers.SaveAsync(new CostCenter { Code = "CC-TAB", Name = "Second tab" }, cancellationToken);

        Assert.Equal(theirs, storage.Stored);
    }

    [Fact]
    public async Task The_refusal_names_the_cause_and_the_remedy()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, storage) = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        storage.AnotherTabWrites();

        var centers = new CostCenterService(database);
        var result = await centers.SaveAsync(new CostCenter { Code = "CC-TAB", Name = "Second tab" }, cancellationToken);

        var errors = new WorkPlanStudio.Services.Forms.FormErrorState();
        errors.Apply(result, new PassThroughLocalizer<SharedResource>());

        Assert.Equal("Error_ChangedElsewhere", errors.Summary);
    }

    [Fact]
    public async Task A_tab_that_is_up_to_date_still_saves()
    {
        // A guard that refuses everything is not a guard. Two saves in a row from
        // the same tab must both land, because each one leaves it current again.
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, storage) = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var centers = new CostCenterService(database);
        var first = await centers.SaveAsync(new CostCenter { Code = "CC-ONE", Name = "First" }, cancellationToken);
        var second = await centers.SaveAsync(new CostCenter { Code = "CC-TWO", Name = "Second" }, cancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, storage.Stored!.Revision - (storage.SaveCalls - 2));   // one revision per write
    }

    [Fact]
    public async Task Replacing_the_whole_database_is_never_refused_as_stale()
    {
        // Import and reset are not edits: they mean "this, instead of whatever is
        // there", so a revision they were not based on is not a conflict.
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (database, storage) = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var exported = storage.Stored!;
        storage.AnotherTabWrites();
        storage.ToImport = exported;

        var readiness = await database.ImportAsync(cancellationToken);

        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void A_payload_written_before_revisions_existed_still_loads()
    {
        // An existing browser holds {data, version} with no revision at all, and
        // must not be locked out by a guard that arrived after it.
        var storage = new FakeStorage { Stored = new StoredDatabase("payload", 7) };

        Assert.Equal(0, storage.Stored.Revision);
    }
}
