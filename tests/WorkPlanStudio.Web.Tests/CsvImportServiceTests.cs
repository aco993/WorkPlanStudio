using System.Text;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The dry run, the commit, and the promise that the second does exactly what
/// the first said it would.
/// </summary>
public sealed class CsvImportServiceTests
{
    private const string CostCentres =
        "Code;Name;Description;Active\n" +
        "CC-7100;Prototyping;First articles;yes\n" +
        "CC-7200;Finishing;;yes\n" +
        "CC-7300;Retired;Kept for history;no\n";

    // ----- the dry run ----------------------------------------------------

    [Fact]
    public async Task A_dry_run_counts_what_would_be_added_and_writes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "dry-run.db");
        var before = shop.Storage.SaveCalls;

        var plan = await shop.PlanAsync(CostCentres, new ImportOptions(ImportEntityKind.CostCenters), cancellationToken);

        Assert.Equal(3, plan.CreateCount);
        Assert.Equal(0, plan.UpdateCount);
        Assert.Empty(plan.Rejected);

        // Nothing written: no snapshot taken, and the rows are not there.
        Assert.Equal(before, shop.Storage.SaveCalls);
        Assert.DoesNotContain(
            await shop.CostCenters.GetAllAsync(cancellationToken),
            costCenter => costCenter.Code == "CC-7100");
    }

    /// <summary>
    /// The contract of the whole feature. The commit is given the plan the user
    /// read and is not allowed to reach a different answer.
    /// </summary>
    [Fact]
    public async Task The_commit_does_exactly_what_the_dry_run_said()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "promise.db");

        var plan = await shop.PlanAsync(CostCentres, new ImportOptions(ImportEntityKind.CostCenters), cancellationToken);
        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);

        Assert.True(committed.IsSuccess, ImportTestSupport.Describe(committed));
        var outcome = committed.Value!;
        Assert.Equal(plan.CreateCount, outcome.Created);
        Assert.Equal(plan.UpdateCount, outcome.Updated);
        Assert.Equal(plan.SkipCount, outcome.Skipped);
        Assert.Equal(plan.Rejected.Count, outcome.Rejected);

        var stored = await shop.CostCenters.GetAllAsync(cancellationToken);
        Assert.Equal("Prototyping", stored.Single(costCenter => costCenter.Code == "CC-7100").Name);
        Assert.Null(stored.Single(costCenter => costCenter.Code == "CC-7200").Description);
        Assert.False(stored.Single(costCenter => costCenter.Code == "CC-7300").IsActive);
    }

    [Fact]
    public async Task An_existing_key_is_skipped_unless_updating_was_asked_for()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "skip.db");

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-2000;Renamed by a file\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);

        Assert.Equal(1, plan.SkipCount);
        Assert.Equal(0, plan.UpdateCount);
        Assert.Equal(ImportRowAction.Skip, plan.Rows.Single().Action);

        // And with nothing to do, the commit refuses rather than reporting a
        // successful import of nothing.
        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);
        Assert.Equal(ApplicationResultStatus.Conflict, committed.Status);
        Assert.Equal("Machining", (await shop.CostCenters.GetAllAsync(cancellationToken))
            .Single(costCenter => costCenter.Code == "CC-2000").Name);
    }

    [Fact]
    public async Task Updating_an_existing_row_is_opt_in_and_always_warned_about()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "update.db");

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-2000;Renamed by a file\n",
            new ImportOptions(ImportEntityKind.CostCenters, UpdateExisting: true),
            cancellationToken);

        Assert.Equal(1, plan.UpdateCount);
        Assert.Contains(plan.Warnings, warning => warning.MessageKey == "Import_Warn_Overwrite");

        Assert.True((await shop.Importer.CommitAsync(plan, cancellationToken)).IsSuccess);
        Assert.Equal("Renamed by a file", (await shop.CostCenters.GetAllAsync(cancellationToken))
            .Single(costCenter => costCenter.Code == "CC-2000").Name);
    }

    // ----- rejections -----------------------------------------------------

    [Fact]
    public async Task A_key_that_appears_twice_in_one_file_is_rejected_the_second_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "duplicate.db");

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-7100;First\nCC-7100;Second\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);

        Assert.Equal(1, plan.CreateCount);
        var rejected = Assert.Single(plan.Rejected);
        Assert.Equal("Import_Error_DuplicateKey", rejected.MessageKey);
        Assert.Equal(3, rejected.Line);
        Assert.Equal(2, rejected.Arguments[1]);   // the line it first appeared on
    }

    /// <summary>
    /// The rejection comes from the same validator the form uses, so the file and
    /// the screen cannot disagree about what a valid cost centre is.
    /// </summary>
    [Fact]
    public async Task A_row_the_form_would_refuse_is_rejected_with_the_form_s_own_message()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "validator.db");

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-7100;\nCC-7200;" + new string('x', 200) + "\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);

        Assert.Equal(0, plan.CreateCount);
        Assert.Equal(["Val_Required", "Val_MaxLength"], plan.Reasons());
        Assert.Equal("Name", plan.Rejected[0].Column);
        Assert.Equal(2, plan.Rejected[0].Line);
    }

    [Fact]
    public async Task A_row_wider_than_the_header_is_rejected_and_names_both_counts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "columns.db");

        var wide = string.Join(';', Enumerable.Repeat("x", 500));
        var plan = await shop.PlanAsync(
            $"Code;Name\nCC-7100;Fine\n{wide}\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);

        Assert.Equal(1, plan.CreateCount);
        var rejected = Assert.Single(plan.Rejected);
        Assert.Equal("Import_Error_ColumnCount", rejected.MessageKey);
        Assert.Equal(2, rejected.Arguments[0]);
        Assert.Equal(500, rejected.Arguments[1]);
    }

    /// <summary>
    /// The other half of the column-count rule: a trimmed trailing column is not
    /// a broken row. Exporters drop them and hand-edited files never had them.
    /// </summary>
    [Fact]
    public async Task A_row_missing_only_an_optional_trailing_column_is_accepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "short-row.db");

        var plan = await shop.PlanAsync(
            "Code;Name;Description\nCC-7100;Prototyping\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);

        Assert.Empty(plan.Rejected);
        Assert.Equal(1, plan.CreateCount);
    }

    [Fact]
    public async Task A_rejected_row_still_appears_in_the_preview()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "preview-rejects.db");

        var plan = await shop.PlanAsync(
            "Code;Name\nCC-7100;Fine\n;No code\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);

        Assert.Equal(2, plan.Rows.Count);
        Assert.Equal(ImportRowAction.Reject, plan.Rows.Single(row => row.Line == 3).Action);
    }

    // ----- file-level refusals -------------------------------------------

    [Fact]
    public async Task An_empty_file_is_refused_by_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "empty.db");

        var inspected = await shop.Importer.InspectAsync(Array.Empty<byte>(), null, cancellationToken);

        Assert.Equal(ApplicationResultStatus.ValidationFailed, inspected.Status);
        Assert.Equal("Import_Error_Empty", inspected.ValidationIssues!.Single().MessageKey);
    }

    [Fact]
    public async Task A_header_only_file_reports_no_data_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "header-only.db");

        var inspected = await shop.Importer.InspectAsync(
            Encoding.UTF8.GetBytes("Code;Name\n"), null, cancellationToken);

        Assert.True(inspected.IsSuccess);
        Assert.Equal(0, inspected.Value!.DataRecords);
    }

    /// <summary>
    /// A spreadsheet, an image or a database file. It is caught before the parser
    /// runs, because the parser would happily read it as one enormous row of
    /// nonsense and the message would then be about a column.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_text_at_all_is_refused_before_it_is_parsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "binary.db");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

        var inspected = await shop.Importer.InspectAsync(png, null, cancellationToken);

        Assert.Equal("Import_Error_NotText", inspected.ValidationIssues!.Single().MessageKey);
    }

    [Fact]
    public async Task A_file_over_the_stated_limit_is_refused_at_the_door()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "too-large.db");

        var inspected = await shop.Importer.InspectAsync(
            new byte[CsvImportService.MaxFileBytes + 1], null, cancellationToken);

        Assert.Equal("Import_Error_TooLarge", inspected.ValidationIssues!.Single().MessageKey);
    }

    [Fact]
    public async Task An_unterminated_quote_is_reported_with_its_line_rather_than_thrown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "quote.db");

        var inspected = await shop.Importer.InspectAsync(
            Encoding.UTF8.GetBytes("Code;Name\nCC-1;\"never closed\n"), null, cancellationToken);

        var issue = inspected.ValidationIssues!.Single();
        Assert.Equal("Import_Parse_UnterminatedQuote", issue.MessageKey);
        Assert.Equal(2, issue.Arguments[0]);
    }

    // ----- encodings, end to end -----------------------------------------

    /// <summary>
    /// A German Excel export: Windows-1252, semicolons, decimal commas, umlauts.
    /// If any one of the three guesses is wrong this row lands wrong rather than
    /// failing, which is why it is asserted all the way to the stored value.
    /// </summary>
    [Fact]
    public async Task A_windows_1252_german_export_lands_with_its_umlauts_and_its_rate_intact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "german.db");

        var content = Encoding.Latin1.GetBytes(
            "Kennung;Bezeichnung;Kostenstelle;Stundensatz;Schichtmodell\r\n" +
            "GRD-900;Flächenschleifer Süd;CC-3000;74,50;one-shift\r\n");

        var header = await shop.Importer.InspectAsync(content, null, cancellationToken);
        Assert.True(header.IsSuccess);
        Assert.Equal(';', header.Value!.Dialect.Separator);

        var map = ColumnMap.Guess(ImportSchemas.For(ImportEntityKind.WorkCenters), header.Value.Columns);
        var planned = await shop.Importer.PlanAsync(
            content, new ImportOptions(ImportEntityKind.WorkCenters), map, null, cancellationToken);
        Assert.True(planned.IsSuccess, ImportTestSupport.Describe(planned));
        Assert.True((await shop.Importer.CommitAsync(planned.Value!, cancellationToken)).IsSuccess);

        var imported = (await shop.Centers.GetAllAsync(cancellationToken)).Single(center => center.Code == "GRD-900");
        Assert.Equal("Flächenschleifer Süd", imported.Name);
        Assert.Equal(74.50m, imported.HourlyRate);
        Assert.Equal("CC-3000", imported.CostCenter!.Code);
    }

    [Fact]
    public async Task A_utf16_export_with_tabs_is_read_the_same_way()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "utf16.db");

        var encoding = new UnicodeEncoding(false, true);
        var content = (byte[])[
            .. encoding.GetPreamble(),
            .. encoding.GetBytes("Code\tName\r\nCC-7400\tPrüfmittelbau\r\n")];

        var header = await shop.Importer.InspectAsync(content, null, cancellationToken);
        Assert.True(header.IsSuccess);
        Assert.Equal('\t', header.Value!.Dialect.Separator);
        Assert.Equal("Code", header.Value.Columns[0]);

        var map = ColumnMap.Guess(ImportSchemas.For(ImportEntityKind.CostCenters), header.Value.Columns);
        var planned = await shop.Importer.PlanAsync(
            content, new ImportOptions(ImportEntityKind.CostCenters), map, null, cancellationToken);
        Assert.True(planned.IsSuccess, ImportTestSupport.Describe(planned));
        Assert.True((await shop.Importer.CommitAsync(planned.Value!, cancellationToken)).IsSuccess);

        Assert.Equal(
            "Prüfmittelbau",
            (await shop.CostCenters.GetAllAsync(cancellationToken)).Single(c => c.Code == "CC-7400").Name);
    }

    /// <summary>
    /// A byte-order mark that is not consumed becomes part of the first header
    /// cell, and nothing matches "Code" any more. The symptom is a mapping screen
    /// that mysteriously cannot find the first column.
    /// </summary>
    [Fact]
    public async Task A_byte_order_mark_does_not_end_up_inside_the_first_header_cell()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "bom.db");

        var content = (byte[])[.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Code;Name\nCC-7500;Tooling\n")];

        var header = await shop.Importer.InspectAsync(content, null, cancellationToken);

        Assert.Equal("Code", header.Value!.Columns[0]);
        Assert.True(header.Value.Dialect.HasByteOrderMark);
    }

    [Fact]
    public async Task An_explicit_separator_overrides_the_guess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "override.db");

        // Detection would read one column here; the user says otherwise.
        var content = Encoding.UTF8.GetBytes("Code,Name\n");

        var semicolon = await shop.Importer.InspectAsync(content, ';', cancellationToken);

        Assert.Equal(';', semicolon.Value!.Dialect.Separator);
        Assert.Single(semicolon.Value.Columns);
    }

    // ----- hostile content ------------------------------------------------

    /// <summary>
    /// A formula cell is imported as the text it is — nothing here evaluates
    /// anything — and the rejected-rows report, which is the one file this
    /// feature writes, defuses it rather than handing it back live.
    /// </summary>
    [Fact]
    public async Task A_formula_cell_is_stored_as_text_and_neutralised_in_the_report()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "formula.db");
        const string Payload = "=cmd|'/C calc'!A0";

        var plan = await shop.PlanAsync(
            $"Code;Name\nCC-7600;{Payload}\n",
            new ImportOptions(ImportEntityKind.CostCenters),
            cancellationToken);
        Assert.True((await shop.Importer.CommitAsync(plan, cancellationToken)).IsSuccess);

        var stored = (await shop.CostCenters.GetAllAsync(cancellationToken)).Single(c => c.Code == "CC-7600");
        Assert.Equal(Payload, stored.Name);

        var report = RejectedRowsReport.Build(["Line", "Column", "Reason"], [(2, "Name", Payload)]);
        Assert.Contains("'=cmd", report, StringComparison.Ordinal);
        Assert.DoesNotContain("\n=cmd", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rejected_report_is_utf8_with_a_mark_so_a_spreadsheet_reads_the_umlauts()
    {
        var bytes = RejectedRowsReport.ToBytes(RejectedRowsReport.Build(["Zeile"], [(2, "Bezeichnung", "Größe")]));

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Contains("Größe", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_containing_the_report_separator_or_a_quote_is_quoted()
    {
        Assert.Equal("\"a;b\"", RejectedRowsReport.Escape("a;b"));
        Assert.Equal("\"say \"\"hi\"\"\"", RejectedRowsReport.Escape("say \"hi\""));
        Assert.Equal("plain", RejectedRowsReport.Escape("plain"));
    }
}
