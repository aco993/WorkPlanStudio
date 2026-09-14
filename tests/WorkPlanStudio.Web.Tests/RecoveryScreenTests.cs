using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.Services.Remote;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The screen a user meets when their stored database cannot be opened. It is the
/// one place in the app where the only available actions are destructive, and it
/// used to offer exactly one of them: reset. Exporting wrote a payload that
/// nothing could ever read back, and neither button asked who was pressing it.
/// </summary>
public sealed class RecoveryScreenTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    /// <summary>A database whose stored payload comes from a schema this build cannot open.</summary>
    private (BrowserDatabase Database, FakeStorage Storage) Unreadable(string name)
    {
        var storage = new FakeStorage { Stored = new StoredDatabase("payload-from-an-older-build", 2) };
        return (_files.CreateDatabase(name, storage), storage);
    }

    private void Arrange(WorkspaceRole role, BrowserDatabase database)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(database);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());

        // A successful import renders the whole app, layout included, so the
        // offline API options the layout's account menu reads have to be here —
        // the same value `AddOptionalApi` registers when nothing is configured.
        Services.AddSingleton(ApiOptions.Offline);

        // Recovery ends by rendering the real router, so the home page's own
        // dependencies have to exist. They are doubles: this test is about the
        // recovery screen, not about what the app draws afterwards.
        Services.AddSingleton<IProductionScheduleService>(new FakeScheduleService());
        Services.AddSingleton<WorkPlanService>();
        Services.AddSingleton<WorkCenterService>();
        Services.AddSingleton<CostCenterService>();
        Services.AddSingleton<ProductionOrderService>();
        Services.AddSingleton<PlantSettingsService>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    [Fact]
    public void A_planner_is_offered_export_import_and_reset()
    {
        var (database, _) = Unreadable("planner-recovery.db");
        Arrange(WorkspaceRole.Planner, database);

        var cut = Render<App>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".recovery-card")));

        var labels = cut.FindAll(".recovery-card button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Storage_Export", labels);
        Assert.Contains("Md_Storage_Import", labels);
        Assert.Contains("Storage_Reset", labels);
    }

    /// <summary>
    /// The service refuses a guest either way — this is the courtesy layer, and its
    /// absence is what let a read-only persona reach the most destructive operation
    /// in the app from a screen nobody had guarded.
    /// </summary>
    [Fact]
    public void A_guest_may_export_and_nothing_else()
    {
        var (database, _) = Unreadable("guest-recovery.db");
        Arrange(WorkspaceRole.Guest, database);

        var cut = Render<App>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".recovery-card")));

        var labels = cut.FindAll(".recovery-card button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Storage_Export"], labels);
    }

    [Fact]
    public async Task Importing_a_readable_payload_recovers_the_app()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (database, storage) = Unreadable("import-recovery.db");
        storage.ToImport = await HealthyPayloadAsync(cancellationToken);

        Arrange(WorkspaceRole.Planner, database);
        var cut = Render<App>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".recovery-card")));

        await cut.InvokeAsync(() =>
            cut.FindAll(".recovery-card button").Single(b => b.TextContent.Trim() == "Md_Storage_Import").Click());

        // Recovered: the recovery card is gone and the router took over.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".recovery-card")));
    }

    /// <summary>A payload written by this build, produced through the real save path.</summary>
    private async Task<StoredDatabase> HealthyPayloadAsync(CancellationToken cancellationToken)
    {
        var storage = new FakeStorage();
        var database = _files.CreateDatabase($"healthy-{Guid.NewGuid():N}.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        Assert.True((await database.PersistAsync(cancellationToken)).IsSuccess);
        return storage.Stored!;
    }
}
