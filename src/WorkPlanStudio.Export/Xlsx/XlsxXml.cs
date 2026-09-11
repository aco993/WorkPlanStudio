using System.Text;

namespace WorkPlanStudio.Export.Xlsx;

/// <summary>
/// The two text chores every part of the package needs: XML escaping and
/// A1-style cell references.
/// </summary>
internal static class XlsxXml
{
    // The two permanently unassigned code points. XML 1.0 has no representation
    // for them - not even a numeric character reference - so they are dropped;
    // Excel treats a file containing one as corrupt rather than ignoring it.
    private const char NotACharacterLow = '￾';
    private const char NotACharacterHigh = '￿';

    /// <summary>
    /// Escapes text for an XML text node or attribute value, and drops the
    /// control characters XML 1.0 cannot carry.
    /// </summary>
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\t' or '\n' or '\r': builder.Append(character); break;
                default:
                    if (character >= ' ' && character != NotACharacterLow && character != NotACharacterHigh)
                        builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>The A1 reference of a zero-based row and column: (0, 0) is A1, (0, 26) is AA1.</summary>
    public static string CellReference(int rowIndex, int columnIndex)
    {
        var builder = new StringBuilder(4);
        for (int remaining = columnIndex; ; remaining = remaining / 26 - 1)
        {
            builder.Insert(0, (char)('A' + remaining % 26));
            if (remaining < 26)
                break;
        }

        return builder.Append(rowIndex + 1).ToString();
    }
}
