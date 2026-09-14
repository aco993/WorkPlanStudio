using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// What happens when the durable half of a save fails, who is allowed to destroy
/// the database, and whether the values that come back are the values that went in.
/// </summary>
public sealed class PersistenceTests
{
    private static WorkCenter NewCenter(string code) =>
        new() { Code = code, Name = code, ParallelCapacity = 1, HourlyRate = 10m };

    [Fact]
    public async Task A_failed_snapshot_rolls_the_change_back_instead_of_leaving_it_live()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("rollback.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var centers = new WorkCenterService(database);

        storage.QuotaExceeded = true;
        var result = await centers.SaveAsync(NewCenter("GHOST-1"), cancellationToken);

        // StorageFull rather than PersistenceFailed: the storage layer has always
        // known which of the two it was, and the surface now carries it, because
        // "no room left" is the one storage failure a person can act on.
        Assert.Equal(ApplicationResultStatus.StorageFull, result.Status);

        // The commit went to SQLite before the snapshot was attempted. Without the
        // rollback the row is live for the rest of the session and gone after a
        // reload, while the page says the save failed — the worst of both.
        Assert.DoesNotContain(await centers.GetAllAsync(cancellationToken), center => center.Code == "GHOST-1");
    }

    [Fact]
    public async Task A_payload_over_the_storage_budget_is_refused_with_its_own_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("quota.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        storage.QuotaExceeded = true;
        var persisted = await database.PersistAsync(cancellationToken);

        // Distinct from WriteFailed because it is the one write failure the
        // visitor can act on: export, free some space, try again.
        Assert.False(persisted.IsSuccess);
        Assert.Equal(BrowserDatabaseFailure.QuotaExceeded, persisted.Failure);
    }

    [Fact]
    public async Task A_guest_may_not_wipe_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("guest-reset.db", storage, guard: new RoleGuard(WorkspaceRole.Guest));
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var before = storage.ClearCalls;

        var reset = await database.ResetAsync(cancellationToken);

        // The docs claimed "a guard on every mutating service" while the most
        // destructive operation in the app asked nobody, and the recovery screen
        // offered it to anyone who could reach it.
        Assert.False(reset.IsReady);
        Assert.Equal(BrowserDatabaseFailure.Forbidden, reset.Failure);
        Assert.Equal(before, storage.ClearCalls);
        Assert.NotEmpty(await new WorkCenterService(database).GetAllAsync(cancellationToken));
    }

    [Fact]
    public async Task A_guest_may_not_import_a_payload_over_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage { ToImport = new StoredDatabase("irrelevant", SchemaUpgrades.CurrentVersion) };
        var database = files.CreateDatabase("guest-import.db", storage, guard: new RoleGuard(WorkspaceRole.Guest));
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var imported = await database.ImportAsync(cancellationToken);

        Assert.Equal(BrowserDatabaseFailure.Forbidden, imported.Failure);
    }

    [Fact]
    public async Task A_planner_may_reset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("planner-reset.db", new FakeStorage(), guard: new RoleGuard(WorkspaceRole.Planner));
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        Assert.True((await database.ResetAsync(cancellationToken)).IsReady);
    }

    [Fact]
    public async Task Decimals_round_trip_exactly_and_are_stored_as_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var factory = files.CreateFactory("decimals.db");
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        db.WorkCenters.Add(new WorkCenter { Code = "DEC-1", Name = "Decimals", ParallelCapacity = 1, HourlyRate = 78.129m });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        Assert.Equal(78.129m, (await db.WorkCenters.SingleAsync(cancellationToken)).HourlyRate);

        // decimal(10,2) gives SQLite NUMERIC affinity, which converts EF's text
        // into a binary double on the way in: the declared type was decorative
        // and the value was not the value.
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT typeof(HourlyRate) FROM WorkCenters;";
        Assert.Equal("text", (await command.ExecuteScalarAsync(cancellationToken))?.ToString());
    }

    [Fact]
    public async Task Numeric_check_constraints_still_compare_as_numbers_on_a_text_column()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var factory = files.CreateFactory("checks.db");
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        // The trap the column-type change leaves behind: without the CAST in the
        // constraint this is a string comparison, and '9' > '1000000' — so an
        // out-of-range rate would be accepted and an in-range one refused.
        db.WorkCenters.Add(new WorkCenter { Code = "OVER", Name = "Over", ParallelCapacity = 1, HourlyRate = 2_000_000m });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellationToken));
        db.ChangeTracker.Clear();

        db.WorkCenters.Add(new WorkCenter { Code = "NINE", Name = "Nine", ParallelCapacity = 1, HourlyRate = 9m });
        await db.SaveChangesAsync(cancellationToken);
        Assert.Equal(9m, (await db.WorkCenters.SingleAsync(w => w.Code == "NINE", cancellationToken)).HourlyRate);
    }

    [Fact]
    public async Task Planning_dates_round_trip_as_wall_clock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("dates.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var orders = new ProductionOrderService(database);
        var plans = new WorkPlanService(database);
        var released = (await plans.GetAllAsync(cancellationToken)).First(p => p.Status == WorkPlanStatus.Released);

        var saved = await orders.SaveAsync(new ProductionOrder
        {
            OrderNumber = "PO-TZ",
            WorkPlanId = released.Id,
            Quantity = 1,
            Priority = 1,
            // A control or a test may hand the service a Kind it has no business
            // having; the value must not move because of it.
            ReleaseLocal = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DueLocal = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)
        }, cancellationToken);
        Assert.True(saved.IsSuccess);

        var reloaded = await orders.GetAsync(saved.Value!.Id, cancellationToken);
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0), reloaded!.ReleaseLocal);
        Assert.Equal(DateTimeKind.Unspecified, reloaded.ReleaseLocal.Kind);
        Assert.Equal(new DateTime(2026, 6, 8, 0, 0, 0), reloaded.DueLocal);
    }

    /// <summary>
    /// The time model is "plant-local wall clock, converted nowhere". A single
    /// conversion added later would move one field and not the others, which is
    /// exactly the bug the rename was meant to prevent — so it is asserted over
    /// the sources rather than left as a comment.
    /// </summary>
    [Fact]
    public void No_source_file_converts_between_local_time_and_utc()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio"), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".razor", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (File: file, Number: index + 1, Line: line)))
            .Where(entry => entry.Line.Contains("ToLocalTime(", StringComparison.Ordinal)
                         || entry.Line.Contains("ToUniversalTime(", StringComparison.Ordinal))
            .Select(entry => $"{Path.GetFileName(entry.File)}:{entry.Number}")
            .ToArray();

        Assert.True(offenders.Length == 0, "time-zone conversions: " + string.Join(", ", offenders));
    }

    [Fact]
    public void A_validation_issue_with_arguments_equals_an_identical_one()
    {
        var first = new ValidationIssue("LotSize", "Val_Range", 1, 100);
        var second = new ValidationIssue("LotSize", "Val_Range", 1, 100);

        // Record equality compares object[] by reference, so Distinct() used to
        // deduplicate only the argument-less issues — the opposite of what the
        // validators need, since the repeated ones all carry arguments.
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Single(new[] { first, second }.Distinct());
        Assert.NotEqual(first, new ValidationIssue("LotSize", "Val_Range", 1, 200));
    }
}
