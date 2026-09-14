using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Cost centres as master data, and the invariants that only became expressible
/// once the work centre pointed at a row instead of repeating a string.
/// </summary>
public sealed class CostCenterTests
{
    private static async Task<(BrowserDatabase Database, CostCenterService CostCenters, WorkCenterService Centers)>
        ArrangeAsync(TempDatabaseFiles files, string name)
    {
        var database = files.CreateDatabase(name, new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        return (database, new CostCenterService(database), new WorkCenterService(database));
    }

    [Fact]
    public async Task The_seed_shares_one_cost_centre_between_two_work_centres()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (_, costCenters, centers) = await ArrangeAsync(files, "seed.db");

        var machining = Assert.Single(await costCenters.GetAllAsync(cancellationToken), c => c.Code == "CC-2000");
        var sharing = (await centers.GetAllAsync(cancellationToken))
            .Where(center => center.CostCenterId == machining.Id)
            .Select(center => center.Code)
            .Order()
            .ToArray();

        // A value several rows deliberately share is an entity, not an attribute.
        Assert.Equal(["CNC-200", "CNC-300"], sharing);
        Assert.Equal(2, (await costCenters.GetUsageCountsAsync(cancellationToken))[machining.Id]);
    }

    [Fact]
    public async Task A_code_that_differs_only_in_case_is_the_same_cost_centre()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (_, costCenters, _) = await ArrangeAsync(files, "case.db");

        var duplicate = await costCenters.SaveAsync(
            new CostCenter { Code = " cc-2000 ", Name = "Machining again" }, cancellationToken);

        Assert.Equal(ApplicationResultStatus.Conflict, duplicate.Status);
        Assert.Contains(duplicate.ValidationIssues!, issue => issue.MessageKey == "Val_CostCenterCodeTaken");
    }

    [Fact]
    public async Task The_unique_index_refuses_the_duplicate_even_without_the_service()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var factory = files.CreateFactory("unique.db");
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        db.CostCenters.Add(new CostCenter { Code = "CC-1", Name = "One" });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        // The collation is the point: Code got NOCASE and a unique index, which
        // is exactly what the free-text column it replaces never had.
        db.CostCenters.Add(new CostCenter { Code = "cc-1", Name = "One again" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task A_cost_centre_in_use_cannot_be_deleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (_, costCenters, _) = await ArrangeAsync(files, "in-use.db");
        var machining = Assert.Single(await costCenters.GetAllAsync(cancellationToken), c => c.Code == "CC-2000");

        var blocked = await costCenters.DeleteAsync(machining.Id, cancellationToken);

        Assert.Equal(ApplicationResultStatus.Conflict, blocked.Status);
        Assert.Contains(blocked.ValidationIssues!, issue => issue.MessageKey == "Val_CostCenterInUse");
    }

    [Fact]
    public async Task An_unused_cost_centre_can_be_created_assigned_and_then_deleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (_, costCenters, centers) = await ArrangeAsync(files, "lifecycle.db");

        var created = await costCenters.SaveAsync(
            new CostCenter { Code = "cc-7000", Name = "Packing", Description = "Line 4" }, cancellationToken);
        Assert.True(created.IsSuccess);

        var stored = Assert.Single(await costCenters.GetAllAsync(cancellationToken), c => c.Id == created.Value);
        Assert.Equal("CC-7000", stored.Code);   // business keys are stored in one canonical spelling

        var center = Assert.Single(await centers.GetAllAsync(cancellationToken), c => c.Code == "SAW-10");
        center.CostCenterId = stored.Id;
        Assert.True((await centers.SaveAsync(center, cancellationToken)).IsSuccess);

        var assigned = Assert.Single(await centers.GetAllAsync(cancellationToken), c => c.Code == "SAW-10");
        Assert.Equal(stored.Id, assigned.CostCenterId);
        Assert.Equal("CC-7000 — Packing", assigned.CostCenter!.Display);

        Assert.Equal(ApplicationResultStatus.Conflict, (await costCenters.DeleteAsync(stored.Id, cancellationToken)).Status);

        assigned.CostCenterId = null;
        Assert.True((await centers.SaveAsync(assigned, cancellationToken)).IsSuccess);
        Assert.True((await costCenters.DeleteAsync(stored.Id, cancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task A_work_centre_cannot_point_at_a_cost_centre_that_is_gone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (_, _, centers) = await ArrangeAsync(files, "dangling.db");
        var center = Assert.Single(await centers.GetAllAsync(cancellationToken), c => c.Code == "SAW-10");

        center.CostCenterId = 9999;
        var result = await centers.SaveAsync(center, cancellationToken);

        Assert.Equal(ApplicationResultStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationIssues!, issue => issue.MessageKey == "Val_CostCenterMissing");
    }

    [Fact]
    public async Task A_retired_cost_centre_stays_in_the_picker_for_the_row_that_uses_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var (_, costCenters, _) = await ArrangeAsync(files, "retired.db");
        var quality = Assert.Single(await costCenters.GetAllAsync(cancellationToken), c => c.Code == "CC-9000");

        quality.IsActive = false;
        Assert.True((await costCenters.SaveAsync(quality, cancellationToken)).IsSuccess);

        // Dropping it would silently rewrite the work centre that points at it the
        // next time anyone opened the row and pressed Save.
        Assert.DoesNotContain(await costCenters.GetSelectableAsync(cancellationToken: cancellationToken), c => c.Id == quality.Id);
        Assert.Contains(await costCenters.GetSelectableAsync(quality.Id, cancellationToken), c => c.Id == quality.Id);
    }
}
