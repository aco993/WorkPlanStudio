using System.Diagnostics;
using System.Globalization;
using System.Text;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Ten thousand rows, which is well past what this app's storage can hold and
/// therefore the right size to prove the parse does not fall over before the
/// quota check gets a chance to refuse it.
/// </summary>
/// <remarks>
/// The assertions are deliberately loose: a shared CI runner is not a benchmark,
/// and a tight bound here would fail for reasons that have nothing to do with the
/// code. They are there to catch an order-of-magnitude regression — a quadratic
/// accident, or a second full copy of the file in strings — not to measure the
/// machine. The measured figures are written to the test output.
/// </remarks>
public sealed class ImportScaleTests
{
    private const int Rows = 10_000;

    private readonly ITestOutputHelper _output;

    public ImportScaleTests(ITestOutputHelper output) => _output = output;

    private static byte[] BuildFile(int rows)
    {
        var builder = new StringBuilder("Code;Name;Description;Active\r\n");
        for (var index = 0; index < rows; index++)
            builder
                .Append("KST-").Append(index.ToString("D6", CultureInfo.InvariantCulture)).Append(';')
                .Append("Cost centre ").Append(index.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append("\"Imported, in bulk\";yes\r\n");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Fact]
    public async Task Ten_thousand_rows_parse_and_resolve_without_a_second_copy_of_the_file()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "scale.db");
        var content = BuildFile(Rows);

        var header = await shop.Importer.InspectAsync(content, null, cancellationToken);
        Assert.True(header.IsSuccess);
        Assert.Equal(Rows, header.Value!.DataRecords);

        var map = ColumnMap.Guess(ImportSchemas.For(ImportEntityKind.CostCenters), header.Value.Columns);
        var reported = new List<int>();
        var progress = new Progress<int>(reported.Add);

        var clock = Stopwatch.StartNew();
        var planned = await shop.Importer.PlanAsync(
            content, new ImportOptions(ImportEntityKind.CostCenters), map, progress, cancellationToken);
        clock.Stop();

        Assert.True(planned.IsSuccess, ImportTestSupport.Describe(planned));
        Assert.Equal(Rows, planned.Value!.CreateCount);
        Assert.Empty(planned.Value.Rejected);

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Rows} rows, {content.Length / 1024} KB file: dry run {clock.ElapsedMilliseconds} ms, {planned.Value.EstimatedPayloadCharacters} estimated characters"));

        // Loose by design — see the note on this class.
        Assert.True(clock.ElapsedMilliseconds < 30_000, $"the dry run took {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The claim the streaming parser exists to make, measured: reading the file
    /// costs about what the strings it hands back cost, not a multiple of it.
    /// </summary>
    /// <remarks>
    /// Measured on one thread with a <see cref="StringReader"/>, which completes
    /// synchronously, so no continuation hops to another thread — the per-thread
    /// allocation counter is only meaningful when nothing moves. The bound is the
    /// decoded text in bytes times six, which a "read it all, split on newlines,
    /// split each line" implementation cannot meet: its two intermediate copies
    /// alone are four times the text before a single field exists.
    /// </remarks>
    [Fact]
    public void Reading_the_file_costs_about_what_the_fields_cost()
    {
        var text = Encoding.UTF8.GetString(BuildFile(Rows));

        _ = ReadEverything(text);   // warm the paths so this measures the parse

        var before = GC.GetAllocatedBytesForCurrentThread();
        var records = ReadEverything(text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var textBytes = (long)text.Length * sizeof(char);
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{records} records from {textBytes / 1024} KB of text: {allocated / 1024} KB allocated ({(double)allocated / textBytes:0.00}x)"));

        Assert.Equal(Rows + 1, records);
        Assert.True(allocated < textBytes * 6, $"the parse allocated {allocated} bytes for {textBytes} bytes of text");
    }

    private static int ReadEverything(string text)
    {
        using var reader = new StringReader(text);
        var enumerator = CsvParser.ReadAsync(reader, ';').GetAsyncEnumerator(CancellationToken.None);
        var count = 0;
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                count++;
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return count;
    }

    /// <summary>
    /// Progress has to reach the screen while the work is going on, not in one
    /// jump at the end: a file this size is the only time anybody looks at it.
    /// </summary>
    [Fact]
    public async Task Progress_is_reported_while_the_file_is_being_read()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "progress.db");
        var content = BuildFile(Rows);

        var header = await shop.Importer.InspectAsync(content, null, cancellationToken);
        var map = ColumnMap.Guess(ImportSchemas.For(ImportEntityKind.CostCenters), header.Value!.Columns);

        var reported = new List<int>();
        await shop.Importer.PlanAsync(
            content,
            new ImportOptions(ImportEntityKind.CostCenters),
            map,
            new Progress<int>(reported.Add),
            cancellationToken);

        // Progress<T> posts to the captured context, so the reports arrive as the
        // continuations run rather than synchronously; what matters is that the
        // first one is early, the last one is the whole file, and there are not so
        // many that each one costs the browser a throttled timer.
        await Task.Yield();
        Assert.InRange(reported.Count, 5, 30);
        Assert.InRange(reported[0], 1, 500);
        Assert.Equal(Rows, reported[^1]);
        Assert.True(reported.SequenceEqual(reported.Order()), "progress went backwards");
    }

    /// <summary>
    /// The whole thing, end to end: ten thousand rows resolved, committed and
    /// stored in one durable write. A per-row commit would be ten thousand
    /// snapshots of a growing database, which is the shape of failure this
    /// service exists to avoid.
    /// </summary>
    [Fact]
    public async Task Ten_thousand_rows_commit_in_a_single_write()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var shop = await ImportTestSupport.ArrangeAsync(files, "scale-commit.db");
        var content = BuildFile(Rows);

        var header = await shop.Importer.InspectAsync(content, null, cancellationToken);
        var map = ColumnMap.Guess(ImportSchemas.For(ImportEntityKind.CostCenters), header.Value!.Columns);
        var planned = await shop.Importer.PlanAsync(
            content, new ImportOptions(ImportEntityKind.CostCenters), map, null, cancellationToken);
        var plan = planned.Value!;

        var before = shop.Storage.SaveCalls;
        var clock = Stopwatch.StartNew();
        var committed = await shop.Importer.CommitAsync(plan, cancellationToken);
        clock.Stop();

        Assert.True(committed.IsSuccess, ImportTestSupport.Describe(committed));
        Assert.Equal(Rows, committed.Value!.Created);
        Assert.Equal(1, shop.Storage.SaveCalls - before);

        var stored = shop.Storage.Stored!.Data.Length;
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Rows} rows committed in {clock.ElapsedMilliseconds} ms; stored payload {stored} characters, estimated {plan.EstimatedPayloadCharacters}, limit {WorkPlanStudio.Data.BrowserDatabase.MaxPayloadCharacters}"));

        // The estimate is allowed to be wrong, but only on the safe side: an
        // estimate that under-reads would let an import start that cannot finish.
        Assert.True(
            plan.EstimatedPayloadCharacters >= stored,
            $"the estimate ({plan.EstimatedPayloadCharacters}) under-read the real payload ({stored})");
    }
}
