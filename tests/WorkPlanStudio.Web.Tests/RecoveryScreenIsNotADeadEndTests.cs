using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.Services.Remote;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Measured on the published site: persona <em>Guest</em>, the stored payload
/// corrupted, reload. The screen said "Export it before resetting the local demo
/// database", the reset is behind <see cref="Permissions.ResetData"/> so the guest
/// could not see it, and the recovery shell drew no navigation, no persona
/// switcher and no language picker — so the guest could not become someone who
/// could. One button, an instruction pointing at a second one that this role never
/// gets, and no way out but the browser's developer tools.
/// </summary>
public sealed class RecoveryScreenIsNotADeadEndTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private BrowserDatabase Arrange(WorkspaceRole role, string name, StoredDatabase stored)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = _files.CreateDatabase(name, new FakeStorage { Stored = stored });
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(database);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(ApiOptions.Offline);
        Services.AddSingleton<IProductionScheduleService>(new FakeScheduleService());
        Services.AddSingleton<WorkPlanService>();
        Services.AddSingleton<WorkCenterService>();
        Services.AddSingleton<CostCenterService>();
        Services.AddSingleton<ProductionOrderService>();
        Services.AddSingleton<PlantSettingsService>();
        return database;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private IRenderedComponent<App> Recovered(WorkspaceRole role, string name)
    {
        Arrange(role, name, new StoredDatabase("payload-from-an-older-build", 2));
        var cut = Render<App>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".recovery-card")));
        return cut;
    }

    [Fact]
    public void A_guest_can_still_change_persona_on_the_recovery_screen()
    {
        var cut = Recovered(WorkspaceRole.Guest, "guest-switch.db");

        // The one control that turns this screen from a dead end into a detour.
        Assert.NotEmpty(cut.FindAll(".persona-option"));
    }

    [Fact]
    public void A_guest_is_not_told_to_press_a_button_only_a_planner_has()
    {
        var cut = Recovered(WorkspaceRole.Guest, "guest-sentence.db");

        var said = cut.FindAll(".recovery-card p").Select(p => p.TextContent.Trim()).ToList();
        Assert.Contains("Storage_RecoveryPreservedReadOnly", said);
        Assert.DoesNotContain("Storage_RecoveryPreserved", said);
    }

    [Fact]
    public void A_planner_reads_the_sentence_about_resetting_because_a_planner_can()
    {
        var cut = Recovered(WorkspaceRole.Planner, "planner-sentence.db");

        var said = cut.FindAll(".recovery-card p").Select(p => p.TextContent.Trim()).ToList();
        Assert.Contains("Storage_RecoveryPreserved", said);
        Assert.DoesNotContain("Storage_RecoveryPreservedReadOnly", said);
    }

    /// <summary>
    /// Storage hands back version 0 for anything it could not read as a payload,
    /// so an unreadable value was reported as "schema version (0) is not
    /// supported" — a version this application has never had. One sentence for two
    /// different situations, and it read as the wrong one.
    /// </summary>
    [Fact]
    public async Task A_value_that_states_no_version_is_not_an_unsupported_version()
    {
        var database = Arrange(
            WorkspaceRole.Planner, "unreadable.db", new StoredDatabase("this is not a payload at all", 0));

        var readiness = await database.EnsureReadyAsync();

        Assert.Equal(BrowserDatabaseFailure.UnreadablePayload, readiness.Failure);
    }

    [Fact]
    public async Task A_version_that_is_stated_and_unsupported_still_says_so()
    {
        var database = Arrange(WorkspaceRole.Planner, "old.db", new StoredDatabase("payload", 2));

        var readiness = await database.EnsureReadyAsync();

        Assert.Equal(BrowserDatabaseFailure.UnsupportedSchema, readiness.Failure);
        Assert.Equal(2, readiness.StoredVersion);
    }

    /// <summary>
    /// The failure says what is wrong; the paragraph under it says what to do,
    /// and that is the one which knows whether the reader may reset at all. On
    /// the published v0.4.0 both said it: "…could not be upgraded. Export it
    /// before resetting." directly above "The existing payload has not been
    /// overwritten. Export it before resetting the local demo database."
    /// </summary>
    [Theory]
    [InlineData("SharedResource.resx", "before resetting")]
    [InlineData("SharedResource.de.resx", "vor dem Zurücksetzen")]
    public void The_failure_message_does_not_repeat_the_advice_below_it(string file, string advice)
    {
        var resource = File.ReadAllText(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Resources", file));

        // The paragraph that owns the advice still carries it.
        Assert.Contains(advice, Value(resource, "Storage_RecoveryPreserved"), StringComparison.OrdinalIgnoreCase);

        foreach (var failure in new[]
        {
            "Storage_Failure_UpgradeFailed", "Storage_Failure_QuotaExceeded",
            "Storage_Failure_UnreadablePayload", "Storage_Failure_UnsupportedSchema",
            "Storage_Failure_InvalidSqlite", "Storage_Failure_TruncatedPayload"
        })
        {
            Assert.DoesNotContain(advice, Value(resource, failure), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_recovery_screen_says_a_thing_once()
    {
        var cut = Recovered(WorkspaceRole.Planner, "once.db");

        var said = cut.FindAll(".recovery-card p").Select(p => p.TextContent.Trim()).ToList();
        Assert.Equal(said.Count, said.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Two situations, two sentences — in both languages, or the fix is half done.</summary>
    [Theory]
    [InlineData("SharedResource.resx")]
    [InlineData("SharedResource.de.resx")]
    public void The_two_failures_do_not_share_a_sentence(string file)
    {
        var resource = File.ReadAllText(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Resources", file));

        var unreadable = Value(resource, "Storage_Failure_UnreadablePayload");
        var unsupported = Value(resource, "Storage_Failure_UnsupportedSchema");

        Assert.NotEqual(unsupported, unreadable);
        Assert.DoesNotContain("{0}", unreadable, StringComparison.Ordinal);
    }

    private static string Value(string resx, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            resx, $"<data name=\"{key}\"[^>]*><value>(?<v>.*?)</value>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, $"{key} is not in the resource file");
        return match.Groups["v"].Value;
    }
}
