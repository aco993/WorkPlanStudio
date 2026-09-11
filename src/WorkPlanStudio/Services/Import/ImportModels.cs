using WorkPlanStudio.Models;

namespace WorkPlanStudio.Services.Import;

/// <summary>What the import would do with one row.</summary>
public enum ImportRowAction
{
    /// <summary>The key is new: a row would be added.</summary>
    Create,

    /// <summary>The key exists and updating was asked for: an existing row would change.</summary>
    Update,

    /// <summary>The key exists and updating was not asked for: nothing happens.</summary>
    Skip,

    /// <summary>The row cannot be imported. The reason is in the rejected list.</summary>
    Reject
}

/// <summary>
/// One thing wrong with one row, located precisely enough to fix it in the file.
/// </summary>
/// <param name="Line">The line in the uploaded file, as the text editor counts them.</param>
/// <param name="Column">The header of the offending column, or <c>null</c> when the row as a whole is wrong.</param>
/// <param name="MessageKey">A key in <c>SharedResource</c> — the service never formats a sentence.</param>
/// <param name="Arguments">Format arguments for the message.</param>
public sealed record ImportIssue(int Line, string? Column, string MessageKey, IReadOnlyList<object> Arguments)
{
    public static ImportIssue At(int line, string? column, string messageKey, params object[] arguments) =>
        new(line, column, messageKey, arguments);
}

/// <summary>One row of the preview table.</summary>
/// <param name="Line">Line in the file.</param>
/// <param name="Action">What would happen to it.</param>
/// <param name="Key">The business key, so the row can be recognised.</param>
/// <param name="Detail">A short description of the row.</param>
public sealed record ImportRowSummary(int Line, ImportRowAction Action, string Key, string Detail);

/// <summary>The choices a user makes before a dry run.</summary>
/// <param name="Kind">Which sheet the file is.</param>
/// <param name="Separator">
/// Overrides the detected separator. Detection is good but it is still a guess,
/// and a one-column file has nothing to detect from.
/// </param>
/// <param name="UpdateExisting">
/// Off by default. With it off, a key that already exists is skipped and counted;
/// with it on, the existing row is overwritten and every such row appears as a
/// warning in the preview. Nothing in this application changes existing data
/// because a file happened to mention it.
/// </param>
/// <param name="CreateMissingCostCenters">
/// Off by default. A work-centre sheet naming a cost centre that does not exist
/// is normally a typo, not an instruction to create master data Controlling owns.
/// </param>
public sealed record ImportOptions(
    ImportEntityKind Kind,
    char? Separator = null,
    bool UpdateExisting = false,
    bool CreateMissingCostCenters = false);

/// <summary>What a file's header row turned out to be.</summary>
/// <param name="Dialect">The encoding and separator that were detected.</param>
/// <param name="Columns">The header cells, in order.</param>
/// <param name="DataRecords">How many non-blank records follow the header.</param>
public sealed record CsvHeader(CsvDialect Dialect, IReadOnlyList<string> Columns, int DataRecords);

/// <summary>What actually happened, once the import was committed.</summary>
/// <param name="Created">Rows added.</param>
/// <param name="Updated">Existing rows changed.</param>
/// <param name="Skipped">Rows whose key already existed, with updating off.</param>
/// <param name="Rejected">Rows that could not be imported.</param>
/// <param name="CostCentersCreated">Cost centres created as a side effect, when that was opted into.</param>
public sealed record ImportOutcome(int Created, int Updated, int Skipped, int Rejected, int CostCentersCreated);

/// <summary>
/// The result of a dry run: everything the commit would do, and nothing written.
/// </summary>
/// <remarks>
/// The plan is also the input to the commit, which is what makes the preview
/// honest. The commit does not re-read the file and does not re-decide anything
/// on its own: it replays this plan against the database inside one transaction
/// and refuses, without writing, if the answer has moved since the preview was
/// taken.
/// </remarks>
public sealed class ImportPlan
{
    internal ImportPlan(
        ImportOptions options,
        CsvDialect dialect,
        StagedRows staged,
        Resolution resolution,
        long currentPayloadCharacters,
        long estimatedPayloadCharacters)
    {
        Options = options;
        Dialect = dialect;
        Staged = staged;
        Rejected = resolution.Rejected;
        Warnings = resolution.Warnings;
        Rows = resolution.Rows;
        CreateCount = resolution.Creates;
        UpdateCount = resolution.Updates;
        SkipCount = resolution.Skips;
        NewCostCenterCount = resolution.NewCostCenters;
        CurrentPayloadCharacters = currentPayloadCharacters;
        EstimatedPayloadCharacters = estimatedPayloadCharacters;
    }

    /// <summary>The options the dry run was made under. The commit must use the same ones.</summary>
    public ImportOptions Options { get; }

    /// <summary>What the file turned out to be.</summary>
    public CsvDialect Dialect { get; }

    internal StagedRows Staged { get; }

    /// <summary>Rows that would be added.</summary>
    public int CreateCount { get; }

    /// <summary>Existing rows that would change.</summary>
    public int UpdateCount { get; }

    /// <summary>Rows that exist already and would be left alone.</summary>
    public int SkipCount { get; }

    /// <summary>Cost centres that would be created as a side effect.</summary>
    public int NewCostCenterCount { get; }

    /// <summary>Every rejected row, with its line, column and reason.</summary>
    public IReadOnlyList<ImportIssue> Rejected { get; }

    /// <summary>Everything that would change existing data, or that was silently assumed.</summary>
    public IReadOnlyList<ImportIssue> Warnings { get; }

    /// <summary>The preview table.</summary>
    public IReadOnlyList<ImportRowSummary> Rows { get; }

    /// <summary>How many characters the stored payload occupies now.</summary>
    public long CurrentPayloadCharacters { get; }

    /// <summary>
    /// A conservative estimate of what it would occupy afterwards. An estimate,
    /// not a measurement: the real figure is only known once SQLite has written
    /// the pages, which is after the point of no return. It is here to turn "the
    /// tab threw" into a sentence, and the measured limit still guards the write.
    /// </summary>
    public long EstimatedPayloadCharacters { get; }

    /// <summary>True when the estimate does not fit the browser's storage budget.</summary>
    public bool ExceedsStorage => EstimatedPayloadCharacters > Data.BrowserDatabase.MaxPayloadCharacters;

    /// <summary>Rows the import would touch.</summary>
    public int AffectedCount => CreateCount + UpdateCount;

    /// <summary>Whether there is anything to commit that would fit.</summary>
    public bool CanCommit => AffectedCount > 0 && !ExceedsStorage;
}

/// <summary>A cost-centre row as the file stated it, before anything is resolved.</summary>
internal sealed record PendingCostCenter(int Line, string Code, string Name, string? Description, bool IsActive);

/// <summary>A work-centre row, naming its cost centre by code.</summary>
internal sealed record PendingWorkCenter(
    int Line,
    string Code,
    string Name,
    string CostCenterCode,
    decimal HourlyRate,
    int ParallelCapacity,
    string ShiftPatternKey,
    bool IsActive);

/// <summary>One operation of a plan, naming its work centre by code.</summary>
internal sealed record PendingOperation(
    int Line,
    int Number,
    string Description,
    string WorkCenterCode,
    decimal SetupMinutes,
    decimal RunMinutes,
    string? Remarks);

/// <summary>A work plan gathered from however many rows named it.</summary>
internal sealed record PendingPlan(
    int Line,
    string PlanNumber,
    string PartNumber,
    string PartName,
    string? Revision,
    WorkPlanStatus Status,
    int LotSize,
    List<PendingOperation> Operations);

/// <summary>An order row, naming its routing by plan number.</summary>
internal sealed record PendingOrder(
    int Line,
    string OrderNumber,
    string PlanNumber,
    int Quantity,
    DateTime ReleaseLocal,
    DateTime DueLocal,
    int Priority);

/// <summary>
/// The file, parsed and grouped, with nothing resolved against the database yet.
/// Exactly one of the lists is populated.
/// </summary>
internal sealed class StagedRows
{
    public List<PendingCostCenter> CostCenters { get; } = [];

    public List<PendingWorkCenter> WorkCenters { get; } = [];

    public List<PendingPlan> Plans { get; } = [];

    public List<PendingOrder> Orders { get; } = [];

    /// <summary>
    /// The header cell each mapped field came from, captured while staging so a
    /// message produced later can still name a column in the user's own file.
    /// </summary>
    public Dictionary<string, string> Columns { get; } = new(StringComparer.Ordinal);

    /// <summary>Rows rejected before the database was consulted — bad numbers, bad dates, short rows.</summary>
    public List<ImportIssue> Rejected { get; } = [];

    /// <summary>Total characters of cell text kept, for the storage estimate.</summary>
    public long TextCharacters { get; set; }
}

/// <summary>What resolving the staged rows against the database says.</summary>
internal sealed class Resolution
{
    public List<ImportIssue> Rejected { get; } = [];

    public List<ImportIssue> Warnings { get; } = [];

    public List<ImportRowSummary> Rows { get; } = [];

    public int Creates { get; set; }

    public int Updates { get; set; }

    public int Skips { get; set; }

    public int NewCostCenters { get; set; }

    /// <summary>The three numbers a commit must still agree with.</summary>
    public (int Creates, int Updates, int Skips) Counts => (Creates, Updates, Skips);
}
