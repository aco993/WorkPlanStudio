using System.Text;
using WorkPlanStudio.Data;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The rules a file is not allowed to route around. Every one of these is a rule
/// the forms already enforce; a bulk path that quietly dropped them would be a
/// second, weaker application sharing the same database.
/// </summary>
public sealed class ImportInvariantTests
{
    // ----- work centres and their cost centres ---------------------------

    [Fact]
    public async Task A_work_centre_naming_an_unknown_cost_centre_is_rejected_rather_than_inventing_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "unknown-cc.db");

        var plan = await shop.PlanAsync(
            "Code;Name;Cost center\nMIL-800;Bed mill;CC-8888\n",
            new ImportOptions(ImportEntityKind.WorkCenters),
            cancellationToken);

        Assert.Equal(0, plan.CreateCount);
        var rejected = Assert.Single(plan.Rejected);
        Assert.Equal("Import_Error_UnknownCostCenter", rejected.MessageKey);
        Assert.Equal("CC-8888", rejected.Arguments[0]);
    }

    [Fact]
    public async Task Creating_the_missing_cost_centre_is_opt_in_counted_and_warned_about()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "create-cc.db");

        var plan = await shop.PlanAsync(
            "Code;Name;Cost center\nMIL-800;Bed mill;CC-8888\n",
            new ImportOptions(ImportEntityKind.WorkCenters, CreateMissingCostCenters: true),
            cancellationToken);

        Assert.Equal(1, plan.NewCostCenterCount);
        Assert.Contains(plan.Warnings, warning => warning.MessageKey == "Import_Warn_NewCostCenter");

        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);
        Assert.True(committed.IsSuccess, ImportTestSupport.Describe(committed));
        Assert.Equal(1, committed.Value!.CostCentersCreated);

        var created = (await shop.CostCenters.GetAllAsync(cancellationToken)).Single(c => c.Code == "CC-8888");
        Assert.Equal("CC-8888", created.Name);   // the file never carried one
        Assert.Equal(created.Id, (await shop.Centers.GetAllAsync(cancellationToken))
            .Single(center => center.Code == "MIL-800").CostCenterId);
    }

    /// <summary>
    /// The guard the work-centre form has: a machine a released order is still
    /// routed through cannot be switched off. Reaching that state through a file
    /// would make the order disappear from the schedule as a preparation error.
    /// </summary>
    [Fact]
    public async Task A_file_cannot_deactivate_a_work_centre_a_released_order_still_needs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "deactivate.db");

        var plan = await shop.PlanAsync(
            "Code;Name;Active\nCNC-300;5-Axis Milling Center;no\n",
            new ImportOptions(ImportEntityKind.WorkCenters, UpdateExisting: true),
            cancellationToken);

        Assert.Equal(0, plan.UpdateCount);
        Assert.Equal("Val_WorkCenterOrderUse", Assert.Single(plan.Rejected).MessageKey);
        Assert.True((await shop.Centers.GetAllAsync(cancellationToken))
            .Single(center => center.Code == "CNC-300").IsActive);
    }

    [Fact]
    public async Task An_unknown_shift_pattern_is_rejected_by_the_same_validator_the_form_uses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "shift.db");

        var plan = await shop.PlanAsync(
            "Code;Name;Shift pattern\nMIL-800;Bed mill;four-shift\n",
            new ImportOptions(ImportEntityKind.WorkCenters),
            cancellationToken);

        Assert.Equal("Val_ShiftPatternUnknown", Assert.Single(plan.Rejected).MessageKey);
    }

    // ----- work plans -----------------------------------------------------

    private const string PlanRows =
        "Plan no.;Part name;Operation no.;Operation description;Work center;Setup minutes;Minutes per piece\n" +
        "WP-7001;Test bracket;30;Inspect;QC-900;5;1\n" +
        "WP-7001;Test bracket;10;Saw blank;SAW-10;8;0,5\n" +
        "WP-7001;Test bracket;20;Mill contour;CNC-300;30;2,1\n";

    /// <summary>
    /// A plan spans rows, and a file has no order of its own: the shop floor runs
    /// operations by number, not by the order a spreadsheet saved them in.
    /// </summary>
    [Fact]
    public async Task A_plan_is_gathered_from_its_rows_and_its_operations_are_ordered_by_number()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "grouping.db");

        var plan = await shop.PlanAsync(PlanRows, new ImportOptions(ImportEntityKind.WorkPlans), cancellationToken);

        Assert.Equal(1, plan.CreateCount);
        Assert.True((await shop.Importer.CommitAsync(plan, cancellationToken)).IsSuccess);

        var imported = (await shop.Plans.GetAllAsync(cancellationToken)).Single(p => p.PlanNumber == "WP-7001");
        Assert.Equal([10, 20, 30], imported.Operations.Select(operation => operation.OperationNumber));
        Assert.Equal("Saw blank", imported.Operations[0].Description);
        Assert.Equal(0.5m, imported.Operations[0].TimePerPieceMinutes);
    }

    [Fact]
    public async Task Two_rows_that_disagree_about_the_same_plan_reject_the_second_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "conflict.db");

        var plan = await shop.PlanAsync(
            "Plan no.;Part name;Operation no.;Operation description;Work center\n" +
            "WP-7002;Bracket;10;Saw;SAW-10\n" +
            "WP-7002;Something else;20;Mill;CNC-300\n",
            new ImportOptions(ImportEntityKind.WorkPlans),
            cancellationToken);

        var rejected = Assert.Single(plan.Rejected);
        Assert.Equal("Import_Error_PlanHeaderConflict", rejected.MessageKey);
        Assert.Equal(3, rejected.Line);
        Assert.Equal("Part name", rejected.Column);
    }

    [Fact]
    public async Task An_operation_number_repeated_within_one_plan_rejects_the_plan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "dup-op.db");

        var plan = await shop.PlanAsync(
            "Plan no.;Part name;Operation no.;Operation description;Work center\n" +
            "WP-7003;Bracket;10;Saw;SAW-10\n" +
            "WP-7003;Bracket;10;Mill;CNC-300\n",
            new ImportOptions(ImportEntityKind.WorkPlans),
            cancellationToken);

        Assert.Equal(0, plan.CreateCount);
        Assert.Equal("Import_Error_DuplicateOperation", Assert.Single(plan.Rejected).MessageKey);
    }

    [Fact]
    public async Task An_operation_naming_a_work_centre_that_does_not_exist_rejects_the_plan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "unknown-wc.db");

        var plan = await shop.PlanAsync(
            "Plan no.;Part name;Operation no.;Operation description;Work center\n" +
            "WP-7004;Bracket;10;Saw;NOPE-1\n",
            new ImportOptions(ImportEntityKind.WorkPlans),
            cancellationToken);

        Assert.Equal(0, plan.CreateCount);
        var rejected = Assert.Single(plan.Rejected);
        Assert.Equal("Import_Error_UnknownWorkCenter", rejected.MessageKey);
        Assert.Equal("NOPE-1", rejected.Arguments[0]);
    }

    /// <summary>
    /// Replacing the operations of a routing that orders were raised from is
    /// allowed — the editor allows it too — but it is never quiet. The count of
    /// affected orders is on the preview before the button is pressed.
    /// </summary>
    [Fact]
    public async Task Restructuring_a_plan_that_orders_depend_on_is_warned_about_with_the_order_count()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "plan-orders.db");

        var plan = await shop.PlanAsync(
            "Plan no.;Part name;Status;Operation no.;Operation description;Work center\n" +
            "WP-1001;Drive shaft Ø20;Released;10;Cut raw bar to length;SAW-10\n",
            new ImportOptions(ImportEntityKind.WorkPlans, UpdateExisting: true),
            cancellationToken);

        var warning = Assert.Single(plan.Warnings, w => w.MessageKey == "Import_Warn_PlanHasOrders");
        Assert.Equal("WP-1001", warning.Arguments[0]);
        Assert.Equal(1, warning.Arguments[1]);
    }

    [Fact]
    public async Task A_german_status_word_is_understood()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "status.db");

        var plan = await shop.PlanAsync(
            "Plan no.;Part name;Status;Operation no.;Operation description;Work center\n" +
            "WP-7005;Bracket;freigegeben;10;Saw;SAW-10\n",
            new ImportOptions(ImportEntityKind.WorkPlans),
            cancellationToken);

        Assert.True((await shop.Importer.CommitAsync(plan, cancellationToken)).IsSuccess);
        Assert.Equal(
            WorkPlanStatus.Released,
            (await shop.Plans.GetAllAsync(cancellationToken)).Single(p => p.PlanNumber == "WP-7005").Status);
    }

    // ----- production orders ---------------------------------------------

    [Fact]
    public async Task An_imported_order_is_a_draft_and_carries_no_routing_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "orders.db");

        var plan = await shop.PlanAsync(
            "Order no.;Work plan;Quantity;Release date;Due date;Priority\n" +
            "PO-7001;WP-1001;1.200;01.06.2026;2026-07-03;3\n",
            new ImportOptions(ImportEntityKind.ProductionOrders),
            cancellationToken);

        Assert.True((await shop.Importer.CommitAsync(plan, cancellationToken)).IsSuccess);

        var order = (await shop.Orders.GetAllAsync(cancellationToken)).Single(o => o.OrderNumber == "PO-7001");
        Assert.Equal(ProductionOrderStatus.Draft, order.Status);
        Assert.Equal("", order.RoutingSnapshotJson);
        Assert.Equal(1200, order.Quantity);
        Assert.Equal(new DateTime(2026, 6, 1), order.ReleaseLocal);
        Assert.Equal(new DateTime(2026, 7, 3), order.DueLocal);
        Assert.Equal(DateTimeKind.Unspecified, order.ReleaseLocal.Kind);
    }

    /// <summary>
    /// The snapshot on a released order is the record of what the shop floor was
    /// told to build. The form refuses to edit it; a file does not get to either.
    /// </summary>
    [Fact]
    public async Task A_released_order_is_never_touched_by_an_import()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "released-order.db");
        var released = (await shop.Orders.GetAllAsync(cancellationToken))
            .First(order => order.Status == ProductionOrderStatus.Released);
        var snapshot = released.RoutingSnapshotJson;

        var plan = await shop.PlanAsync(
            "Order no.;Work plan;Quantity;Release date;Due date\n" +
            $"{released.OrderNumber};WP-1001;9;01.06.2026;02.06.2026\n",
            new ImportOptions(ImportEntityKind.ProductionOrders, UpdateExisting: true),
            cancellationToken);

        Assert.Equal(0, plan.UpdateCount);
        Assert.Equal("Val_OrderNotDraft", Assert.Single(plan.Rejected).MessageKey);

        var after = (await shop.Orders.GetAllAsync(cancellationToken))
            .Single(order => order.OrderNumber == released.OrderNumber);
        Assert.Equal(snapshot, after.RoutingSnapshotJson);
        Assert.Equal(released.Quantity, after.Quantity);
    }

    [Fact]
    public async Task An_order_against_a_plan_that_is_not_released_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "archived-plan.db");

        var plan = await shop.PlanAsync(
            "Order no.;Work plan;Quantity;Release date;Due date\n" +
            "PO-7002;WP-1004;5;01.06.2026;02.06.2026\n" +      // WP-1004 is archived
            "PO-7003;WP-9999;5;01.06.2026;02.06.2026\n",       // and this one does not exist
            new ImportOptions(ImportEntityKind.ProductionOrders),
            cancellationToken);

        Assert.Equal(0, plan.CreateCount);
        Assert.Equal(["Val_PlanNotReleased", "Val_WorkPlanMissing"], plan.Reasons());
    }

    [Fact]
    public async Task A_due_date_before_the_release_is_rejected_by_the_order_validator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "dates.db");

        var plan = await shop.PlanAsync(
            "Order no.;Work plan;Quantity;Release date;Due date\n" +
            "PO-7004;WP-1001;5;10.06.2026;01.06.2026\n" +
            "PO-7005;WP-1001;5;10/06/2026;01.07.2026\n",
            new ImportOptions(ImportEntityKind.ProductionOrders),
            cancellationToken);

        Assert.Equal(["Val_DueBeforeRelease", "Import_Error_NotADate"], plan.Reasons());
        Assert.Equal("Release date", plan.Rejected[1].Column);
    }

    // ----- permission -----------------------------------------------------

    [Fact]
    public async Task A_guest_cannot_commit_an_import()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(
            files, "guest.db", new RoleGuard(WorkspaceRole.Guest));

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-7900;Guest attempt\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);

        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);

        Assert.Equal(ApplicationResultStatus.Forbidden, committed.Status);
        Assert.DoesNotContain(await shop.CostCenters.GetAllAsync(cancellationToken), c => c.Code == "CC-7900");
    }

    /// <summary>
    /// The policy follows the sheet, not the feature: a supervisor raises orders
    /// and does not own master data, exactly as on the pages.
    /// </summary>
    [Fact]
    public async Task A_supervisor_may_import_orders_but_not_master_data()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(
            files, "supervisor.db", new RoleGuard(WorkspaceRole.Supervisor));

        var masterData = await shop.PlanAsync(
            "Code;Name\nCC-7910;Nope\n", new ImportOptions(ImportEntityKind.CostCenters), cancellationToken);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await shop.Importer.CommitAsync(masterData, cancellationToken)).Status);

        var orders = await shop.PlanAsync(
            "Order no.;Work plan;Quantity;Release date;Due date\nPO-7910;WP-1001;5;01.06.2026;02.06.2026\n",
            new ImportOptions(ImportEntityKind.ProductionOrders),
            cancellationToken);
        Assert.True((await shop.Importer.CommitAsync(orders, cancellationToken)).IsSuccess);
    }

    [Fact]
    public void The_policy_for_each_sheet_is_the_one_the_matching_page_uses()
    {
        Assert.Equal(Permissions.ManageMasterData, CsvImportService.PolicyFor(ImportEntityKind.CostCenters));
        Assert.Equal(Permissions.ManageMasterData, CsvImportService.PolicyFor(ImportEntityKind.WorkCenters));
        Assert.Equal(Permissions.ManageMasterData, CsvImportService.PolicyFor(ImportEntityKind.WorkPlans));
        Assert.Equal(Permissions.ManageOrders, CsvImportService.PolicyFor(ImportEntityKind.ProductionOrders));
    }

    // ----- all or nothing -------------------------------------------------

    /// <summary>
    /// The transactional guarantee, forced rather than argued: the durable write
    /// fails after SQLite has already committed, and the database must go back to
    /// what it was — not stay changed for the rest of the session and vanish on
    /// the next reload.
    /// </summary>
    [Fact]
    public async Task A_persistence_failure_mid_import_leaves_the_database_exactly_as_it_was()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "rollback.db");
        var before = (await shop.CostCenters.GetAllAsync(cancellationToken)).Count;

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-7920;One\nCC-7930;Two\nCC-7940;Three\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);
        Assert.Equal(3, plan.CreateCount);

        shop.Storage.QuotaExceeded = true;
        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);
        shop.Storage.QuotaExceeded = false;

        Assert.Equal(ApplicationResultStatus.PersistenceFailed, committed.Status);

        var after = await shop.CostCenters.GetAllAsync(cancellationToken);
        Assert.Equal(before, after.Count);
        Assert.DoesNotContain(after, costCenter => costCenter.Code.StartsWith("CC-79", StringComparison.Ordinal));
    }

    /// <summary>
    /// Not one row at a time. The whole import is one durable write, so there is
    /// no window in which half of it is stored.
    /// </summary>
    [Fact]
    public async Task A_whole_import_is_a_single_write_to_browser_storage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "one-write.db");

        var rows = string.Concat(Enumerable.Range(1, 40).Select(index => $"CC-80{index:D2};Cell {index}\n"));
        var plan = await shop.PlanAsync(
            "Code;Name\n" + rows, new ImportOptions(ImportEntityKind.CostCenters), cancellationToken);

        var before = shop.Storage.SaveCalls;
        Assert.True((await shop.Importer.CommitAsync(plan, cancellationToken)).IsSuccess);

        Assert.Equal(1, shop.Storage.SaveCalls - before);
        Assert.Equal(40, plan.CreateCount);
    }

    /// <summary>
    /// The estimate refuses the import before a minute of work rather than after
    /// it. The measured limit still guards the write — this is the courtesy.
    /// </summary>
    [Fact]
    public async Task An_import_that_would_not_fit_browser_storage_is_refused_before_anything_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "quota.db");

        // Wide names, enough of them that the estimate passes the 4.5 M character
        // ceiling. Each is valid on its own; it is the total that does not fit.
        var filler = new string('x', 100);
        var rows = new StringBuilder("Code;Name\n");
        for (var index = 0; index < 12_000; index++)
            rows.Append("KST-").Append(index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture))
                .Append(';').Append(filler).Append('\n');

        var plan = await shop.PlanAsync(rows.ToString(), new ImportOptions(ImportEntityKind.CostCenters), cancellationToken);

        Assert.True(plan.ExceedsStorage);
        Assert.False(plan.CanCommit);

        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);
        Assert.Equal(ApplicationResultStatus.Conflict, committed.Status);
        Assert.Equal("Import_Error_Quota", committed.ValidationIssues!.Single().MessageKey);
        Assert.DoesNotContain(
            await shop.CostCenters.GetAllAsync(cancellationToken),
            costCenter => costCenter.Code.StartsWith("KST-", StringComparison.Ordinal));
    }

    /// <summary>
    /// The preview is a promise about a database that can move under it. When it
    /// has, the import refuses instead of quietly doing something else.
    /// </summary>
    [Fact]
    public async Task An_import_refuses_when_the_data_changed_after_the_dry_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "raced.db");

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-8500;Later\n", new ImportOptions(ImportEntityKind.CostCenters), cancellationToken);
        Assert.Equal(1, plan.CreateCount);

        // Somebody else creates it in the meantime — another tab, or the form.
        Assert.True((await shop.CostCenters.SaveAsync(
            new CostCenter { Code = "CC-8500", Name = "Created by hand" }, cancellationToken)).IsSuccess);

        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);

        Assert.Equal(ApplicationResultStatus.Conflict, committed.Status);
        Assert.Equal("Import_Error_Changed", committed.ValidationIssues!.Single().MessageKey);
        Assert.Equal("Created by hand", (await shop.CostCenters.GetAllAsync(cancellationToken))
            .Single(costCenter => costCenter.Code == "CC-8500").Name);
    }

    /// <summary>
    /// The import goes through the same storage layer as everything else, so a
    /// reload sees what the import wrote.
    /// </summary>
    [Fact]
    public async Task What_an_import_writes_survives_a_reload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "durable.db");

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-8600;Survives\n", new ImportOptions(ImportEntityKind.CostCenters), cancellationToken);
        Assert.True((await shop.Importer.CommitAsync(plan, cancellationToken)).IsSuccess);

        // A second BrowserDatabase over the same storage is what a reload is.
        var reloaded = files.CreateDatabase("durable-reload.db", shop.Storage);
        Assert.True((await reloaded.EnsureReadyAsync()).IsReady);

        Assert.Contains(
            await new CostCenterService(reloaded).GetAllAsync(cancellationToken),
            costCenter => costCenter.Code == "CC-8600");
    }
}
