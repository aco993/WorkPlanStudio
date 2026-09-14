using System.Text;

namespace WorkPlanStudio.Services.Import;

/// <summary>
/// Writes the rejected rows back out as a CSV the planner can fix and re-import.
/// </summary>
/// <remarks>
/// <para>
/// A list of reasons on a screen is unusable against a thousand-row file: the
/// person fixing it works in the spreadsheet, not in the browser. So the report
/// carries the line number, the column and the sentence, in the language the
/// screen is in.
/// </para>
/// <para>
/// It is also the one place this feature writes a file rather than reading one,
/// which is where CSV injection lives. A cell beginning <c>=</c>, <c>+</c>,
/// <c>-</c>, <c>@</c>, a tab or a carriage return is a formula to Excel, and the
/// content of these cells comes from the file the user was sent. Every such cell
/// is prefixed with an apostrophe, so the report of a hostile file is text and
/// not a payload. The import itself never evaluates anything — a cell is a
/// string on the way in — but a report that hands the formula back would have
/// made the round trip the attack.
/// </para>
/// </remarks>
public static class RejectedRowsReport
{
    private const char Separator = ';';

    /// <summary>
    /// The report as text. UTF-8 with a byte-order mark is what Excel needs to
    /// read umlauts without being asked.
    /// </summary>
    /// <param name="headers">Column titles, already localised.</param>
    /// <param name="rows">One row per rejected line: line number, column, reason.</param>
    public static string Build(IReadOnlyList<string> headers, IEnumerable<(int Line, string Column, string Reason)> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(Separator, headers.Select(Escape)));
        foreach (var (line, column, reason) in rows)
            builder.AppendLine(string.Join(Separator, [
                Escape(line.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Escape(column),
                Escape(reason)
            ]));

        return builder.ToString();
    }

    /// <summary>The bytes to hand to a download, mark included.</summary>
    public static byte[] ToBytes(string report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(report)];
    }

    /// <summary>Quotes a field, and defuses one that a spreadsheet would run.</summary>
    public static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;

        return text.Contains('"', StringComparison.Ordinal)
               || text.Contains(Separator, StringComparison.Ordinal)
               || text.Contains('\n', StringComparison.Ordinal)
               || text.Contains('\r', StringComparison.Ordinal)
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }
}
