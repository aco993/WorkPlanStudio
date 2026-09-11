using CsCheck;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Property-based cover for the parser. Hand-picked cases prove the cases someone
/// thought of; this generates tables full of separators, quotes, line breaks and
/// padding, writes them out as RFC 4180 and asserts the parser hands back exactly
/// what went in. On failure CsCheck shrinks to the smallest table that breaks it
/// and prints a seed to reproduce it.
/// </summary>
public sealed class CsvRoundTripTests
{
    /// <summary>Everything that has ever broken a CSV parser, and two ordinary letters.</summary>
    private static readonly char[] Alphabet = ['a', '0', 'ä', '"', ',', ';', '\t', '\n', '\r', ' '];

    private static readonly Gen<string> GenField =
        from indexes in Gen.Int[0, Alphabet.Length - 1].Array[0, 6]
        select new string([.. indexes.Select(index => Alphabet[index])]);

    private static readonly Gen<string[][]> GenTable =
        from columns in Gen.Int[1, 5]
        from rows in GenField.Array[columns].Array[1, 6]
        select Fix(rows);

    /// <summary>
    /// A record whose every field is blank is a blank line, and the parser drops
    /// those on purpose — that is what makes a leading blank line and a trailing
    /// newline harmless. Such a row cannot round-trip and is not meant to, so the
    /// generator does not produce one.
    /// </summary>
    private static string[][] Fix(string[][] rows)
    {
        foreach (var row in rows)
            if (row.All(string.IsNullOrWhiteSpace))
                row[0] = "x";

        return rows;
    }

    private static string Write(string[][] rows, char separator) =>
        string.Concat(rows.Select(row =>
            string.Join(separator, row.Select(field => Quote(field, separator))) + "\r\n"));

    private static string Quote(string field, char separator) =>
        field.Any(character => character == separator || character is '"' or '\n' or '\r')
            ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : field;

    /// <summary>
    /// A line break inside a quoted field comes back as <c>\n</c> whichever way it
    /// was written. Documented, and harmless here: every field in this app is
    /// single-line and the validators reject control characters outright.
    /// </summary>
    private static string Expected(string field) =>
        field.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static List<CsvRecord> Read(string text, char separator)
    {
        using var reader = new StringReader(text);
        var records = new List<CsvRecord>();
        var enumerator = CsvParser.ReadAsync(reader, separator).GetAsyncEnumerator(CancellationToken.None);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                records.Add(enumerator.Current);
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return records;
    }

    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    [InlineData('\t')]
    public void Any_table_survives_being_written_and_read_back(char separator) =>
        GenTable.Sample(rows =>
        {
            var parsed = Read(Write(rows, separator), separator);

            Assert.Equal(rows.Length, parsed.Count);
            for (var index = 0; index < rows.Length; index++)
                Assert.Equal(rows[index].Select(Expected), parsed[index].Fields);
        });

    /// <summary>
    /// Whatever else it does, the parser must not lose a row or invent one: a
    /// missing row in a thousand-row import is invisible and permanent.
    /// </summary>
    [Fact]
    public void The_record_count_never_moves() =>
        GenTable.Sample(rows =>
        {
            Assert.Equal(rows.Length, Read(Write(rows, ';'), ';').Count);
            Assert.Equal(rows.Length, Read(Write(rows, ';').Replace("\r\n", "\n", StringComparison.Ordinal), ';').Count);
        });

    /// <summary>
    /// Every record reports the line it started on, and a multi-line quoted field
    /// moves the next one along. Off-by-one here makes every message on a file
    /// with a quoted newline point at the wrong row.
    /// </summary>
    [Fact]
    public void Every_record_reports_the_line_it_really_started_on() =>
        GenTable.Sample(rows =>
        {
            var text = Write(rows, ';');
            var parsed = Read(text, ';');

            var line = 1;
            for (var index = 0; index < rows.Length; index++)
            {
                Assert.Equal(line, parsed[index].Line);
                line += 1 + rows[index].Sum(field => Expected(field).Count(character => character == '\n'));
            }
        });
}
