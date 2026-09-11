using System.Text;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The parser, against everything a spreadsheet, a database export or a hostile
/// visitor can put in a file. Every case here is one this app will actually be
/// handed: a German Excel writes semicolons and Windows-1252, a Mac writes bare
/// carriage returns, a hand-edited file has a blank line at the top, and someone
/// eventually uploads a PNG.
/// </summary>
public sealed class CsvParserTests
{
    internal static async Task<List<CsvRecord>> ReadAsync(string text, char separator = ',')
    {
        using var reader = new StringReader(text);
        var records = new List<CsvRecord>();
        await foreach (var record in CsvParser.ReadAsync(reader, separator, TestContext.Current.CancellationToken))
            records.Add(record);

        return records;
    }

    private static string[] FieldsOf(CsvRecord record) => [.. record.Fields];

    [Fact]
    public async Task A_plain_file_becomes_records_with_their_line_numbers()
    {
        var records = await ReadAsync("Code,Name\nCC-1,Machining\nCC-2,Sawing\n");

        Assert.Equal(3, records.Count);
        Assert.Equal(["Code", "Name"], FieldsOf(records[0]));
        Assert.Equal([1, 2, 3], records.Select(record => record.Line));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public async Task Every_line_terminator_ends_exactly_one_record(string terminator)
    {
        var records = await ReadAsync($"a,b{terminator}c,d{terminator}e,f{terminator}");

        Assert.Equal(3, records.Count);
        Assert.Equal([1, 2, 3], records.Select(record => record.Line));
        Assert.Equal(["e", "f"], FieldsOf(records[2]));
    }

    /// <summary>
    /// A file edited on two machines. The CRLF must not be counted as two lines,
    /// or every message after it points at the wrong row.
    /// </summary>
    [Fact]
    public async Task Terminators_may_be_mixed_within_one_file()
    {
        var records = await ReadAsync("a\r\nb\nc\rd");

        Assert.Equal(4, records.Count);
        Assert.Equal([1, 2, 3, 4], records.Select(record => record.Line));
        Assert.Equal(["d"], FieldsOf(records[3]));
    }

    [Fact]
    public async Task A_quoted_field_may_contain_the_separator_a_quote_and_a_newline()
    {
        var records = await ReadAsync("\"Turning, then deburring\",\"a \"\"quoted\"\" word\",\"two\nlines\"\n");

        Assert.Single(records);
        Assert.Equal(["Turning, then deburring", "a \"quoted\" word", "two\nlines"], FieldsOf(records[0]));
    }

    /// <summary>A CRLF inside a quoted field is one line break, and one line.</summary>
    [Fact]
    public async Task A_record_after_a_multiline_field_reports_the_line_it_really_starts_on()
    {
        var records = await ReadAsync("a,\"x\r\ny\",c\r\nd,e,f\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal("x\ny", records[0].Fields[1]);
        Assert.Equal(1, records[0].Line);
        Assert.Equal(3, records[1].Line);
    }

    [Fact]
    public async Task A_leading_blank_line_does_not_become_the_header()
    {
        var records = await ReadAsync("\n\nCode,Name\nCC-1,Machining\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(["Code", "Name"], FieldsOf(records[0]));
        Assert.Equal(3, records[0].Line);
    }

    [Fact]
    public async Task Trailing_empty_columns_are_kept()
    {
        var records = await ReadAsync("a,b,,\n");

        Assert.Equal(["a", "b", "", ""], FieldsOf(records[0]));
    }

    [Fact]
    public async Task A_trailing_newline_does_not_produce_an_empty_record()
    {
        Assert.Single(await ReadAsync("a,b\n"));
        Assert.Single(await ReadAsync("a,b\r\n"));
        Assert.Single(await ReadAsync("a,b"));
    }

    /// <summary>
    /// Not in RFC 4180, and common: <c>"a"b</c>. Refusing it would be correct and
    /// useless — the person holding the file cannot fix the exporter that wrote it.
    /// </summary>
    [Fact]
    public async Task Text_after_a_closing_quote_is_appended_rather_than_refused()
    {
        var records = await ReadAsync("\"a\"b,c\n");

        Assert.Equal(["ab", "c"], FieldsOf(records[0]));
    }

    [Fact]
    public async Task A_quote_in_the_middle_of_an_unquoted_field_is_a_literal_quote()
    {
        var records = await ReadAsync("12\" pipe,x\n");

        Assert.Equal(["12\" pipe", "x"], FieldsOf(records[0]));
    }

    [Fact]
    public async Task An_empty_file_has_no_records() => Assert.Empty(await ReadAsync(""));

    [Fact]
    public async Task A_file_of_nothing_but_blank_lines_has_no_records() =>
        Assert.Empty(await ReadAsync("\r\n\n   \n\r"));

    [Fact]
    public async Task A_header_only_file_has_exactly_one_record() =>
        Assert.Single(await ReadAsync("Code;Name", ';'));

    [Fact]
    public async Task A_blank_line_in_the_middle_is_skipped_without_shifting_the_line_numbers()
    {
        var records = await ReadAsync("a\n\nb\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(1, records[0].Line);
        Assert.Equal(3, records[1].Line);
    }

    // ----- adversarial input ---------------------------------------------

    [Fact]
    public async Task An_unterminated_quote_names_the_line_it_was_opened_on()
    {
        var exception = await Assert.ThrowsAsync<CsvFormatException>(
            () => ReadAsync("a,b\nc,\"never closed\nd,e"));

        Assert.Equal("Import_Parse_UnterminatedQuote", exception.MessageKey);
        Assert.Equal(2, exception.Line);
    }

    [Fact]
    public async Task A_one_megabyte_field_is_refused_by_name_rather_than_allocated()
    {
        var huge = new string('x', 1024 * 1024);

        var exception = await Assert.ThrowsAsync<CsvFormatException>(() => ReadAsync($"a,{huge}\n"));

        Assert.Equal("Import_Parse_FieldTooLong", exception.MessageKey);
        Assert.Equal(CsvParser.MaxFieldCharacters, Assert.Single(exception.Arguments));
    }

    /// <summary>
    /// A field exactly at the limit is fine. The cap is a bound on what a hostile
    /// file can allocate, not a second opinion about the column widths — those are
    /// the validators' business.
    /// </summary>
    [Fact]
    public async Task A_field_at_the_limit_is_still_parsed()
    {
        var records = await ReadAsync(new string('x', CsvParser.MaxFieldCharacters));

        Assert.Equal(CsvParser.MaxFieldCharacters, records[0].Fields[0].Length);
    }

    /// <summary>
    /// 500 columns is not a parse error. It is a row that does not match its
    /// header, which is a judgement the import service makes with the header in
    /// hand — the parser's job is to read it faithfully and say how wide it was.
    /// </summary>
    [Fact]
    public async Task A_row_with_five_hundred_columns_is_parsed_not_refused()
    {
        var records = await ReadAsync(string.Join(',', Enumerable.Range(0, 500)));

        Assert.Equal(500, records[0].Fields.Count);
    }

    [Fact]
    public async Task A_row_wider_than_the_column_cap_is_refused()
    {
        var exception = await Assert.ThrowsAsync<CsvFormatException>(
            () => ReadAsync(string.Join(',', Enumerable.Range(0, CsvParser.MaxColumns + 5))));

        Assert.Equal("Import_Parse_TooManyColumns", exception.MessageKey);
    }

    /// <summary>
    /// A formula cell is text. The parser has no evaluator and must never grow
    /// one; this pins the fact that the dangerous-looking string arrives as
    /// exactly the characters that were in the file.
    /// </summary>
    [Fact]
    public async Task A_formula_cell_is_carried_through_as_plain_text()
    {
        const string Payload = "=cmd|'/C calc'!A0";
        var records = await ReadAsync($"Code,Name\nCC-1,{Payload}\n");

        Assert.Equal(Payload, records[1].Fields[1]);
    }

    /// <summary>
    /// Not a CSV at all: a PNG header. It parses into nonsense rather than
    /// throwing, which is why the import service checks for a NUL byte before it
    /// ever reaches the parser — asserted here so the division of labour is
    /// deliberate rather than accidental.
    /// </summary>
    [Fact]
    public async Task A_binary_file_parses_into_nonsense_rather_than_failing()
    {
        var png = Encoding.Latin1.GetString([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01]);

        var records = await ReadAsync(png);

        Assert.NotEmpty(records);
        Assert.DoesNotContain(records[0].Fields, field => field == "Code");
    }

    [Fact]
    public async Task Reading_stops_when_the_caller_cancels()
    {
        using var source = new CancellationTokenSource();
        using var reader = new StringReader(string.Join('\n', Enumerable.Range(0, 5000)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var seen = 0;
            await foreach (var _ in CsvParser.ReadAsync(reader, ',', source.Token))
                if (++seen == 1)
                    await source.CancelAsync();
        });
    }
}
