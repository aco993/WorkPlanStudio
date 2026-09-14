using System.Text;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services.Import;

/// <summary>
/// Reads master data out of a CSV file a planner already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dry run is the product.</b> Every method here exists so that
/// <see cref="CommitAsync"/> can be boring: the file is parsed, every row is put
/// through the same validators the forms use, and the result is a plan the user
/// reads before anything is written. The commit replays that plan against the
/// database inside a single transaction and refuses if the answer has moved.
/// </para>
/// <para>
/// <b>It writes once.</b> The four master-data services each commit and snapshot
/// per call, which is right for a form and wrong for a file: a thousand rows
/// would be a thousand snapshots and, on the row that fails, five hundred
/// half-imported ones. This stages everything into one context and goes through
/// <see cref="DatabaseMutation"/> once, so the import is one <c>SaveChanges</c>
/// and one durable write, with the pre-image restored when either fails.
/// </para>
/// <para>
/// <b>It does not release anything.</b> Orders are created as drafts and a
/// released order is never touched, because releasing freezes a routing snapshot
/// that the shop floor is then working to. A file cannot express the judgement
/// that act represents — see ADR 0018.
/// </para>
/// </remarks>
public sealed partial class CsvImportService
{
    /// <summary>
    /// The largest file this accepts.
    /// </summary>
    /// <remarks>
    /// The whole database lives in <c>localStorage</c>, which is about 5 MB per
    /// origin and holds the SQLite file Base64-encoded — so roughly 3.3 MB of
    /// database, all of it, for every table. A CSV larger than 8 MB cannot fit
    /// once it is rows and indexes rather than text, so refusing it at the door
    /// is more honest than reading it for a minute and then failing on the write.
    /// </remarks>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Records before the first progress report. Small, so a file that turns out
    /// to be slow says so immediately rather than after a blank second.
    /// </summary>
    private const int FirstProgressInterval = 250;

    /// <summary>
    /// The largest gap between progress reports.
    /// </summary>
    /// <remarks>
    /// The gap doubles after each report, because every report is also a yield
    /// back to the browser's event loop and a yield is not free: a fixed 250-row
    /// interval costs a 30 000-row file 120 of them, and on a throttled tab — a
    /// background one, or a slow phone — a throttled timer turns each into most of
    /// a second. Doubling gives the same immediate feedback on a small file and
    /// about a dozen yields on a large one.
    /// </remarks>
    private const int MaxProgressInterval = 4_000;

    /// <summary>
    /// Base64 turns three bytes into four characters. Applied to the estimate so
    /// the comparison is against the same unit the storage limit is expressed in.
    /// </summary>
    private const double Base64Inflation = 4.0 / 3.0;

    /// <summary>
    /// How much a character of cell text costs once it is a SQLite row: the text
    /// itself, plus page, row-header and index overhead. Deliberately pessimistic
    /// — an estimate that under-reads is worse than useless here.
    /// </summary>
    private const double StorageOverhead = 2.5;

    private readonly BrowserDatabase _db;
    private readonly IBrowserDatabaseStorage _storage;
    private readonly IPermissionGuard _guard;

    public CsvImportService(BrowserDatabase db, IBrowserDatabaseStorage storage, IPermissionGuard? guard = null)
    {
        _db = db;
        _storage = storage;
        _guard = guard ?? AllowAllGuard.Instance;
    }

    /// <summary>The policy a given sheet needs. Orders are the supervisor's; master data is not.</summary>
    public static string PolicyFor(ImportEntityKind kind) =>
        kind == ImportEntityKind.ProductionOrders ? Permissions.ManageOrders : Permissions.ManageMasterData;

    /// <summary>
    /// Reads the header row and reports what the file turned out to be, without
    /// looking at the data. This is what the mapping screen is built from.
    /// </summary>
    public async Task<ApplicationResult<CsvHeader>> InspectAsync(
        ReadOnlyMemory<byte> content,
        char? separator = null,
        CancellationToken cancellationToken = default)
    {
        if (Refuse(content) is { } refusal)
            return ApplicationResult<CsvHeader>.Validation([refusal]);

        var dialect = Detect(content, separator);
        try
        {
            using var reader = OpenReader(content);
            IReadOnlyList<string>? header = null;
            var records = 0;

            await foreach (var record in CsvParser.ReadAsync(reader, dialect.Separator, cancellationToken))
            {
                if (header is null)
                    header = record.Fields;
                else
                    records++;
            }

            if (header is null)
                return ApplicationResult<CsvHeader>.Validation([new ValidationIssue("File", "Import_Error_Empty")]);

            return ApplicationResult<CsvHeader>.Success(new CsvHeader(dialect, header, records));
        }
        catch (CsvFormatException exception)
        {
            return ApplicationResult<CsvHeader>.Validation([ToIssue(exception)]);
        }
    }

    /// <summary>
    /// Parses, validates and resolves the whole file <b>without writing anything</b>,
    /// and returns what a commit would do.
    /// </summary>
    /// <param name="content">The uploaded bytes.</param>
    /// <param name="options">What the user chose.</param>
    /// <param name="map">Which column feeds which field.</param>
    /// <param name="progress">Records read so far, for a file large enough to notice.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<ApplicationResult<ImportPlan>> PlanAsync(
        ReadOnlyMemory<byte> content,
        ImportOptions options,
        ColumnMap map,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(map);

        if (Refuse(content) is { } refusal)
            return ApplicationResult<ImportPlan>.Validation([refusal]);

        var missing = map.MissingRequired();
        if (missing.Count > 0)
            return ApplicationResult<ImportPlan>.Validation(
                [.. missing.Select(field => new ValidationIssue(field.Key, "Import_Error_RequiredColumn", field.LabelKey))]);

        var dialect = Detect(content, options.Separator);

        StagedRows staged;
        try
        {
            staged = await StageAsync(content, dialect, options, map, progress, cancellationToken);
        }
        catch (CsvFormatException exception)
        {
            return ApplicationResult<ImportPlan>.Validation([ToIssue(exception)]);
        }

        Resolution resolution;
        await using (var db = await _db.CreateContextAsync(cancellationToken))
            resolution = await ApplyAsync(db, staged, options, write: false, cancellationToken);

        var stored = await _storage.LoadAsync(cancellationToken);
        var current = stored?.Data.Length ?? 0;
        var estimate = current + (long)(staged.TextCharacters * StorageOverhead * Base64Inflation);

        return ApplicationResult<ImportPlan>.Success(
            new ImportPlan(options, dialect, staged, resolution, current, estimate));
    }

    /// <summary>
    /// Writes the plan. One transaction, one durable snapshot, and the database
    /// file put back where it was if either half fails.
    /// </summary>
    public async Task<ApplicationResult<ImportOutcome>> CommitAsync(
        ImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!await _guard.CanAsync(PolicyFor(plan.Options.Kind), cancellationToken))
            return ApplicationResult<ImportOutcome>.Forbidden();

        // The measured limit is enforced by the storage layer, which restores the
        // pre-image when it refuses. This is the estimate, and it exists so the
        // user is told before a minute of work rather than after it.
        if (plan.ExceedsStorage)
            return ApplicationResult<ImportOutcome>.Conflict(
                new ValidationIssue("File", "Import_Error_Quota", plan.EstimatedPayloadCharacters, BrowserDatabase.MaxPayloadCharacters));

        if (plan.AffectedCount == 0)
            return ApplicationResult<ImportOutcome>.Conflict(new ValidationIssue("File", "Import_Error_NothingToDo"));

        return await DatabaseMutation.RunAsync<ImportOutcome>(
            _db,
            async (db, token) =>
            {
                var applied = await ApplyAsync(db, plan.Staged, plan.Options, write: true, token);

                // The preview is a promise. If resolving the same rows against the
                // database now gives different answers, something changed under
                // the user between reading the preview and pressing the button,
                // and quietly importing the new answer is exactly the surprise the
                // dry run exists to prevent.
                if (applied.Counts != (plan.CreateCount, plan.UpdateCount, plan.SkipCount))
                {
                    db.ChangeTracker.Clear();
                    return ApplicationResult<Func<ImportOutcome>>.Conflict(
                        new ValidationIssue("File", "Import_Error_Changed"));
                }

                return ApplicationResult<Func<ImportOutcome>>.Success(() => new ImportOutcome(
                    applied.Creates,
                    applied.Updates,
                    applied.Skips,
                    applied.Rejected.Count,
                    applied.NewCostCenters));
            },
            new ValidationIssue("File", "Import_Error_Constraint"),
            cancellationToken);
    }

    // ----- file handling -------------------------------------------------

    private static ValidationIssue? Refuse(ReadOnlyMemory<byte> content)
    {
        if (content.Length == 0)
            return new ValidationIssue("File", "Import_Error_Empty");
        if (content.Length > MaxFileBytes)
            return new ValidationIssue("File", "Import_Error_TooLarge", MaxFileBytes / (1024 * 1024));

        // A NUL byte in the first kilobyte of a single-byte encoding means this is
        // not a text file at all - a spreadsheet, an image, a database. UTF-16 is
        // full of them by design, so it is excluded by its own detection first.
        var sniff = content.Span[..Math.Min(1024, content.Length)];
        var (encoding, _) = CsvDialectDetector.DetectEncoding(sniff);
        if (encoding.CodePage != Encoding.Unicode.CodePage
            && encoding.CodePage != Encoding.BigEndianUnicode.CodePage
            && sniff.Contains((byte)0))
            return new ValidationIssue("File", "Import_Error_NotText");

        return null;
    }

    private static CsvDialect Detect(ReadOnlyMemory<byte> content, char? separator)
    {
        var length = Math.Min(CsvDialectDetector.SniffBytes, content.Length);
        var detected = CsvDialectDetector.Detect(content.Span[..length], complete: length == content.Length);
        return separator is { } chosen ? detected with { Separator = chosen } : detected;
    }

    private static StreamReader OpenReader(ReadOnlyMemory<byte> content)
    {
        var (encoding, _) = CsvDialectDetector.DetectEncoding(
            content.Span[..Math.Min(CsvDialectDetector.SniffBytes, content.Length)]);

        // detectEncodingFromByteOrderMarks stays on so the mark is consumed rather
        // than becoming the first character of the first header cell - the classic
        // "why is my first column called ﻿Code" import bug.
        return new StreamReader(
            new MemoryStream(content.ToArray(), writable: false),
            encoding,
            detectEncodingFromByteOrderMarks: true);
    }

    private static ValidationIssue ToIssue(CsvFormatException exception) =>
        new("File", exception.MessageKey, [.. exception.Arguments.Prepend(exception.Line)]);

    // ----- staging -------------------------------------------------------

    private static async Task<StagedRows> StageAsync(
        ReadOnlyMemory<byte> content,
        CsvDialect dialect,
        ImportOptions options,
        ColumnMap map,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var staged = new StagedRows();
        foreach (var field in map.Schema.Fields)
            if (map[field.Key] != ColumnMap.Unmapped)
                staged.Columns[field.Key] = map.ColumnName(field.Key);

        var plansByNumber = new Dictionary<string, PendingPlan>(StringComparer.OrdinalIgnoreCase);
        var headerWidth = map.Header.Count;
        var widestRequired = map.Schema.Required.Select(entry => map[entry.Key]).DefaultIfEmpty(-1).Max() + 1;

        using var reader = OpenReader(content);
        var first = true;
        var read = 0;
        var step = FirstProgressInterval;
        var nextReport = FirstProgressInterval;

        await foreach (var record in CsvParser.ReadAsync(reader, dialect.Separator, cancellationToken))
        {
            if (first)
            {
                first = false;
                continue;
            }

            read++;
            if (read >= nextReport)
            {
                progress?.Report(read);

                // Hands the browser back its message loop: without this a large
                // file freezes the tab and the progress it is reporting never
                // reaches the screen.
                await Task.Yield();

                step = Math.Min(step * 2, MaxProgressInterval);
                nextReport = read + step;
            }

            // More fields than the header is a broken row. Fewer is only a broken
            // row when a required column is among the missing ones: exporters and
            // hand-edited files routinely drop trailing empty columns, and an
            // absent optional cell is the same as an empty one.
            if (record.Fields.Count > headerWidth || record.Fields.Count < widestRequired)
            {
                staged.Rejected.Add(ImportIssue.At(
                    record.Line, null, "Import_Error_ColumnCount", headerWidth, record.Fields.Count));
                continue;
            }

            staged.TextCharacters += record.Fields.Sum(field => field.Length) + record.Fields.Count;

            switch (options.Kind)
            {
                case ImportEntityKind.CostCenters:
                    StageCostCenter(staged, map, record);
                    break;
                case ImportEntityKind.WorkCenters:
                    StageWorkCenter(staged, map, record);
                    break;
                case ImportEntityKind.WorkPlans:
                    StagePlanRow(staged, plansByNumber, map, record);
                    break;
                case ImportEntityKind.ProductionOrders:
                    StageOrder(staged, map, record);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options));
            }
        }

        progress?.Report(read);
        return staged;
    }

    private static void StageCostCenter(StagedRows staged, ColumnMap map, CsvRecord record)
    {
        var code = Text.Key(map.Value(record, "Code"));
        if (code.Length == 0)
        {
            staged.Rejected.Add(Missing(record, map, "Code"));
            return;
        }

        var active = true;
        if (map.Value(record, "IsActive") is { Length: > 0 } flag && !ImportValues.TryParseBool(flag, out active))
        {
            staged.Rejected.Add(ImportIssue.At(record.Line, map.ColumnName("IsActive"), "Import_Error_NotABoolean", flag));
            return;
        }

        var description = map.Value(record, "Description");
        staged.CostCenters.Add(new PendingCostCenter(
            record.Line,
            code,
            map.Value(record, "Name"),
            description.Length == 0 ? null : description,
            active));
    }

    private static void StageWorkCenter(StagedRows staged, ColumnMap map, CsvRecord record)
    {
        var code = Text.Key(map.Value(record, "Code"));
        if (code.Length == 0)
        {
            staged.Rejected.Add(Missing(record, map, "Code"));
            return;
        }

        if (!TryDecimal(staged, map, record, "HourlyRate", 0m, out var rate)
            || !TryInt(staged, map, record, "ParallelCapacity", 1, out var capacity)
            || !TryBool(staged, map, record, "IsActive", true, out var active))
            return;

        var pattern = map.Value(record, "ShiftPatternKey");
        staged.WorkCenters.Add(new PendingWorkCenter(
            record.Line,
            code,
            map.Value(record, "Name"),
            Text.Key(map.Value(record, "CostCenterCode")),
            rate,
            capacity,
            pattern.Length == 0 ? ShiftPatterns.Continuous.Key : pattern.Trim(),
            active));
    }

    private static void StagePlanRow(
        StagedRows staged,
        Dictionary<string, PendingPlan> plansByNumber,
        ColumnMap map,
        CsvRecord record)
    {
        var planNumber = Text.Key(map.Value(record, "PlanNumber"));
        if (planNumber.Length == 0)
        {
            staged.Rejected.Add(Missing(record, map, "PlanNumber"));
            return;
        }

        if (!TryInt(staged, map, record, "OperationNumber", 0, out var operationNumber)
            || !TryInt(staged, map, record, "LotSize", 1, out var lotSize)
            || !TryDecimal(staged, map, record, "SetupMinutes", 0m, out var setup)
            || !TryDecimal(staged, map, record, "RunMinutes", 0m, out var run))
            return;

        var statusText = map.Value(record, "Status");
        if (!TryStatus(statusText, out var status))
        {
            staged.Rejected.Add(ImportIssue.At(record.Line, map.ColumnName("Status"), "Import_Error_UnknownStatus", statusText));
            return;
        }

        var revision = map.Value(record, "Revision");
        var remarks = map.Value(record, "Remarks");
        var operation = new PendingOperation(
            record.Line,
            operationNumber,
            map.Value(record, "OperationDescription"),
            Text.Key(map.Value(record, "WorkCenterCode")),
            setup,
            run,
            remarks.Length == 0 ? null : remarks);

        if (plansByNumber.TryGetValue(planNumber, out var existing))
        {
            // A plan spans rows, so its header data is repeated on each of them.
            // Two rows disagreeing about the part name is not a detail to pick a
            // winner for - it means the file says two different things.
            var conflict = Conflicting(existing, map, record, planNumber, status, lotSize, revision);
            if (conflict is not null)
            {
                staged.Rejected.Add(conflict);
                return;
            }

            existing.Operations.Add(operation);
            return;
        }

        plansByNumber[planNumber] = new PendingPlan(
            record.Line,
            planNumber,
            Text.Key(map.Value(record, "PartNumber")),
            map.Value(record, "PartName"),
            revision.Length == 0 ? null : Text.Key(revision),
            status,
            lotSize,
            [operation]);

        staged.Plans.Add(plansByNumber[planNumber]);
    }

    private static ImportIssue? Conflicting(
        PendingPlan existing,
        ColumnMap map,
        CsvRecord record,
        string planNumber,
        WorkPlanStatus status,
        int lotSize,
        string revision)
    {
        var partName = map.Value(record, "PartName");
        var partNumber = Text.Key(map.Value(record, "PartNumber"));
        var normalisedRevision = revision.Length == 0 ? null : Text.Key(revision);

        if (!string.Equals(existing.PartName, partName, StringComparison.Ordinal))
            return ImportIssue.At(record.Line, map.ColumnName("PartName"), "Import_Error_PlanHeaderConflict", planNumber, existing.Line);
        if (!string.Equals(existing.PartNumber, partNumber, StringComparison.Ordinal))
            return ImportIssue.At(record.Line, map.ColumnName("PartNumber"), "Import_Error_PlanHeaderConflict", planNumber, existing.Line);
        if (!string.Equals(existing.Revision ?? "", normalisedRevision ?? "", StringComparison.Ordinal))
            return ImportIssue.At(record.Line, map.ColumnName("Revision"), "Import_Error_PlanHeaderConflict", planNumber, existing.Line);
        if (existing.Status != status)
            return ImportIssue.At(record.Line, map.ColumnName("Status"), "Import_Error_PlanHeaderConflict", planNumber, existing.Line);
        if (existing.LotSize != lotSize)
            return ImportIssue.At(record.Line, map.ColumnName("LotSize"), "Import_Error_PlanHeaderConflict", planNumber, existing.Line);

        return null;
    }

    private static void StageOrder(StagedRows staged, ColumnMap map, CsvRecord record)
    {
        var orderNumber = Text.Key(map.Value(record, "OrderNumber"));
        if (orderNumber.Length == 0)
        {
            staged.Rejected.Add(Missing(record, map, "OrderNumber"));
            return;
        }

        if (!TryInt(staged, map, record, "Quantity", 1, out var quantity)
            || !TryInt(staged, map, record, "Priority", 1, out var priority)
            || !TryDate(staged, map, record, "ReleaseLocal", out var release)
            || !TryDate(staged, map, record, "DueLocal", out var due))
            return;

        staged.Orders.Add(new PendingOrder(
            record.Line,
            orderNumber,
            Text.Key(map.Value(record, "PlanNumber")),
            quantity,
            release,
            due,
            priority));
    }

    // ----- cell helpers --------------------------------------------------

    private static ImportIssue Missing(CsvRecord record, ColumnMap map, string field) =>
        ImportIssue.At(record.Line, map.ColumnName(field), "Val_Required");

    private static bool TryDecimal(
        StagedRows staged, ColumnMap map, CsvRecord record, string field, decimal fallback, out decimal value)
    {
        var text = map.Value(record, field);
        if (text.Length == 0)
        {
            value = fallback;
            return true;
        }

        if (ImportValues.TryParseDecimal(text, out value))
            return true;

        staged.Rejected.Add(ImportIssue.At(record.Line, map.ColumnName(field), "Import_Error_NotANumber", text));
        return false;
    }

    private static bool TryInt(
        StagedRows staged, ColumnMap map, CsvRecord record, string field, int fallback, out int value)
    {
        var text = map.Value(record, field);
        if (text.Length == 0)
        {
            value = fallback;
            return true;
        }

        if (ImportValues.TryParseInt(text, out value))
            return true;

        staged.Rejected.Add(ImportIssue.At(record.Line, map.ColumnName(field), "Import_Error_NotAWholeNumber", text));
        return false;
    }

    private static bool TryBool(
        StagedRows staged, ColumnMap map, CsvRecord record, string field, bool fallback, out bool value)
    {
        var text = map.Value(record, field);
        if (text.Length == 0)
        {
            value = fallback;
            return true;
        }

        if (ImportValues.TryParseBool(text, out value))
            return true;

        staged.Rejected.Add(ImportIssue.At(record.Line, map.ColumnName(field), "Import_Error_NotABoolean", text));
        return false;
    }

    private static bool TryDate(
        StagedRows staged, ColumnMap map, CsvRecord record, string field, out DateTime value)
    {
        var text = map.Value(record, field);
        if (ImportValues.TryParseDate(text, out value))
        {
            value = PlantTime.Wall(value);
            return true;
        }

        staged.Rejected.Add(ImportIssue.At(
            record.Line, map.ColumnName(field), "Import_Error_NotADate", text, ImportValues.AcceptedDateFormats));
        return false;
    }

    private static bool TryStatus(string text, out WorkPlanStatus status)
    {
        status = WorkPlanStatus.Draft;
        if (text.Length == 0)
            return true;

        foreach (var candidate in Enum.GetValues<WorkPlanStatus>())
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }

        // German spellings, so a German export does not have to say "Released".
        switch (text.ToLowerInvariant())
        {
            case "entwurf":
                status = WorkPlanStatus.Draft;
                return true;
            case "freigegeben":
                status = WorkPlanStatus.Released;
                return true;
            case "archiviert":
                status = WorkPlanStatus.Archived;
                return true;
            default:
                return false;
        }
    }
}
