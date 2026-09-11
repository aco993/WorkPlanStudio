using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CsCheck;
using WorkPlanStudio.Export.Csv;
using WorkPlanStudio.Export.Xlsx;

namespace WorkPlanStudio.Export.Tests;

/// <summary>
/// Invariants over generated data rather than hand-picked cases. Escaping is
/// exactly the kind of code where the examples a person thinks of all pass and
/// the one they did not - a quote directly before the separator, a field that is
/// nothing but line breaks - is the one in the customer's part description.
/// </summary>
public class ExportPropertyTests
{
    // Deliberately nasty: every character that means something to one of the
    // two grammars, plus ordinary text and a couple of German letters.
    private const string Alphabet = "ab1 ,;\"'\r\n\t=@+-<>&äöüß€.";

    private static readonly Gen<string> GenField =
        Gen.Int[0, Alphabet.Length - 1].Array[0, 8].Select(indices => new string(indices.Select(i => Alphabet[i]).ToArray()));

    private static readonly Gen<ExportTable> GenTable =
        from columnCount in Gen.Int[1, 4]
        from headers in GenField.Array[columnCount]
        from rows in GenField.Array[columnCount].Array[0, 12]
        select new ExportTable(
            "T",
            [.. headers.Select((h, i) => new ExportColumn(h.Length == 0 ? "c" + i.ToString(CultureInfo.InvariantCulture) : h))],
            [.. rows.Select(IReadOnlyList<ExportCell> (row) => [.. row.Select(ExportCell.OfText)])]);

    [Fact]
    public void Any_text_survives_a_write_and_a_read_of_the_csv()
    {
        (from table in GenTable
         from useSemicolon in Gen.Bool
         select (table, useSemicolon)).Sample(input =>
        {
            var options = new CsvOptions
            {
                Separator = input.useSemicolon ? ';' : ',',
                WriteByteOrderMark = false,
                NeutraliseFormulas = false
            };

            var parsed = CsvTestReader.Parse(CsvWriter.WriteText(input.table, options), options.Separator);
            var expected = Expected(input.table);

            Assert.Equal(expected.Count, parsed.Count);
            for (int row = 0; row < expected.Count; row++)
                Assert.Equal(expected[row], parsed[row]);
        });
    }

    [Fact]
    public void The_formula_guard_is_the_only_thing_that_changes_a_value()
    {
        GenTable.Sample(table =>
        {
            var options = new CsvOptions { Separator = ';', WriteByteOrderMark = false };
            var parsed = CsvTestReader.Parse(CsvWriter.WriteText(table, options), ';');
            var expected = Expected(table);

            for (int row = 0; row < expected.Count; row++)
                for (int column = 0; column < expected[row].Count; column++)
                {
                    var original = expected[row][column];
                    var recovered = parsed[row][column];

                    // Either the value came back untouched, or it came back with
                    // exactly one apostrophe in front - and only when a
                    // spreadsheet would have evaluated it.
                    if (recovered != original)
                    {
                        Assert.True(FormulaGuard.LooksLikeFormula(original), $"unexpected change: '{original}' -> '{recovered}'");
                        Assert.Equal("'" + original, recovered);
                    }
                }
        });
    }

    [Fact]
    public void A_written_field_is_quoted_exactly_when_it_has_to_be()
    {
        (from field in GenField
         from useSemicolon in Gen.Bool
         select (field, separator: useSemicolon ? ';' : ',')).Sample(input =>
        {
            var escaped = CsvWriter.Escape(input.field, input.separator);
            bool quoted = escaped.StartsWith('"');
            bool needsQuoting = input.field.IndexOfAny([input.separator, '"', '\r', '\n']) >= 0;

            Assert.Equal(needsQuoting, quoted);
        });
    }

    [Fact]
    public void Every_shared_string_index_in_a_generated_workbook_resolves()
    {
        GenTable.Sample(table =>
        {
            var probe = new WorkbookProbe(XlsxWriter.Write([table]));
            int available = probe.SharedStrings().Count;

            foreach (var cell in probe.Cells(1).Where(c => c.Attribute("t")?.Value == "s"))
            {
                int index = int.Parse(
                    cell.Element(XName.Get("v", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"))!.Value,
                    CultureInfo.InvariantCulture);

                Assert.InRange(index, 0, available - 1);
            }
        });
    }

    [Fact]
    public void A_generated_workbook_always_has_as_many_rows_as_it_was_given()
    {
        GenTable.Sample(table =>
        {
            var grid = new WorkbookProbe(XlsxWriter.Write([table])).Grid(1);
            Assert.Equal(table.Rows.Count + 1, grid.Count);
        });
    }

    /// <summary>What the CSV should contain: the headers, then every row's text.</summary>
    private static List<IReadOnlyList<string>> Expected(ExportTable table)
    {
        IReadOnlyList<string> headers = [.. table.Columns.Select(c => c.Header)];
        var expected = new List<IReadOnlyList<string>> { headers };
        for (int row = 0; row < table.Rows.Count; row++)
            expected.Add([.. Enumerable.Range(0, table.Columns.Count).Select(c => table.CellAt(row, c).Text ?? "")]);

        return expected;
    }
}

/// <summary>
/// A minimal RFC 4180 reader, written for the round-trip properties only:
/// records end at a CRLF outside quotes, a doubled quote inside a quoted field
/// is one quote. Deliberately not part of the library - reading CSV is the
/// import stream's job, and a reader written from the same head as the writer
/// would share its mistakes if it were anything more than this.
/// </summary>
internal static class CsvTestReader
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text, char separator)
    {
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];

            if (quoted)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            if (character == '"' && field.Length == 0)
                quoted = true;
            else if (character == separator)
                Flush(fields, field);
            else if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                Flush(fields, field);
                records.Add([.. fields]);
                fields.Clear();
                i++;
            }
            else
                field.Append(character);
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            Flush(fields, field);
            records.Add([.. fields]);
        }

        return records;
    }

    private static void Flush(List<string> fields, StringBuilder field)
    {
        fields.Add(field.ToString());
        field.Clear();
    }
}
