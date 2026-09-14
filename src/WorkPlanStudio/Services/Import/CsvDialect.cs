using System.Text;

namespace WorkPlanStudio.Services.Import;

/// <summary>What a spreadsheet actually wrote, as opposed to what it was asked to write.</summary>
/// <param name="Separator">The field separator that was detected.</param>
/// <param name="EncodingName">The encoding that was detected, for the screen.</param>
/// <param name="HasByteOrderMark">Whether the file announced its encoding itself.</param>
public sealed record CsvDialect(char Separator, string EncodingName, bool HasByteOrderMark)
{
    /// <summary>How the separator is written in a message: a tab has no glyph of its own.</summary>
    public string SeparatorLabel => Separator == '\t' ? "TAB" : Separator.ToString();
}

/// <summary>
/// Decides the encoding and the field separator of an uploaded file from its
/// first few kilobytes.
/// </summary>
/// <remarks>
/// Both have to be guessed because the format carries neither. A German Excel
/// writes Windows-1252 with semicolons, the same Excel on a Mac writes UTF-8 with
/// commas, and a database tool's "Unicode text" export writes UTF-16 with tabs.
/// Assuming any one of them turns the other two into a file that imports
/// successfully and wrongly — every row a single field, or every umlaut a
/// replacement character — which is worse than refusing it. So the guess is made
/// explicitly, shown before anything is written, and can be overridden.
/// </remarks>
public static class CsvDialectDetector
{
    /// <summary>
    /// How much of the file is examined. Large enough for a header row and a
    /// handful of data rows in any of the four encodings, small enough that a
    /// hostile 8 MB file costs one buffer rather than a full decode.
    /// </summary>
    public const int SniffBytes = 16 * 1024;

    /// <summary>The separators offered, in the order ties are broken.</summary>
    public static IReadOnlyList<char> Separators { get; } = [';', ',', '\t'];

    /// <param name="sniff">The first <see cref="SniffBytes"/> of the file, or all of it.</param>
    /// <param name="complete"><c>true</c> when <paramref name="sniff"/> is the whole file.</param>
    public static CsvDialect Detect(ReadOnlySpan<byte> sniff, bool complete)
    {
        var (encoding, bomLength) = DetectEncoding(sniff);
        var text = encoding.GetString(sniff[Math.Min(bomLength, sniff.Length)..]);
        return new CsvDialect(DetectSeparator(text, complete), Label(encoding), bomLength > 0);
    }

    /// <summary>
    /// The name shown on screen.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Encoding.EncodingName"/>: that reads its text from the
    /// globalization resources, which a trimmed WebAssembly build does not ship,
    /// so on the published site it renders as <c>Globalization_cp_65001</c>. These
    /// four names are also the ones a person choosing an export format sees.
    /// </remarks>
    public static string Label(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.CodePage switch
        {
            65001 => "UTF-8",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            1252 => "Windows-1252",
            var other => "code page " + other.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <summary>The encoding, plus the length of the byte-order mark to skip.</summary>
    /// <remarks>
    /// The no-mark fallback is the interesting half. UTF-8 is a self-checking
    /// encoding, and Windows-1252 text containing a non-ASCII character almost
    /// never happens to form valid multi-byte sequences, so "decodes as UTF-8"
    /// decides it. Pure ASCII satisfies both and the answer does not matter.
    /// UTF-16 without a mark is recognised from its NUL padding instead, because
    /// it is not valid UTF-8 and would otherwise be read as Windows-1252 with a
    /// NUL between every letter.
    /// </remarks>
    public static (Encoding Encoding, int BomLength) DetectEncoding(ReadOnlySpan<byte> sniff)
    {
        if (sniff.StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]))
            return (new UTF8Encoding(false), 3);
        if (sniff.StartsWith([(byte)0xFF, (byte)0xFE]))
            return (new UnicodeEncoding(false, false), 2);
        if (sniff.StartsWith([(byte)0xFE, (byte)0xFF]))
            return (new UnicodeEncoding(true, false), 2);

        if (LooksLikeUtf16(sniff, oddPadding: true))
            return (new UnicodeEncoding(false, false), 0);
        if (LooksLikeUtf16(sniff, oddPadding: false))
            return (new UnicodeEncoding(true, false), 0);

        return IsValidUtf8(sniff)
            ? (new UTF8Encoding(false), 0)
            : (Windows1252Encoding.Instance, 0);
    }

    /// <summary>Picks the separator that splits the sample into the most consistent table.</summary>
    /// <remarks>
    /// Counting occurrences alone is not enough: one description field containing
    /// "Turning, then deburring" outvotes four semicolons. Each candidate is
    /// scored instead on how many of the sample's lines it splits into the
    /// <em>same</em> number of columns, quoted sections excluded, and only ties go
    /// to the wider table. A file with genuinely one column matches nothing here
    /// and is rejected later by the mapper, which is the honest outcome.
    /// </remarks>
    public static char DetectSeparator(string sample, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var lines = SampleLines(sample, complete);
        var best = Separators[0];
        var bestScore = 0;
        var bestColumns = 0;

        foreach (var candidate in Separators)
        {
            var counts = lines.Select(line => CountOutsideQuotes(line, candidate) + 1).ToArray();
            if (counts.Length == 0)
                continue;

            var widest = counts
                .GroupBy(count => count)
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Key)
                .First();

            if (widest.Key < 2)
                continue;

            var score = widest.Count();
            if (score > bestScore || (score == bestScore && widest.Key > bestColumns))
            {
                best = candidate;
                bestScore = score;
                bestColumns = widest.Key;
            }
        }

        return best;
    }

    /// <summary>
    /// Up to five non-empty lines. The last one is dropped when the sample was cut
    /// off at the buffer edge: a truncated line has the wrong column count for
    /// every candidate and would score them all down equally.
    /// </summary>
    private static List<string> SampleLines(string sample, bool complete)
    {
        var lines = sample
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Trim().Length > 0)
            .ToList();

        if (!complete && lines.Count > 1)
            lines.RemoveAt(lines.Count - 1);

        return lines.Take(5).ToList();
    }

    private static int CountOutsideQuotes(string line, char candidate)
    {
        var count = 0;
        var quoted = false;
        foreach (var character in line)
        {
            if (character == '"')
                quoted = !quoted;
            else if (!quoted && character == candidate)
                count++;
        }

        return count;
    }

    /// <summary>
    /// UTF-16 text that is mostly Latin has a NUL in every second byte. Half the
    /// buffer being NUL on one parity and almost none on the other is not
    /// something a single-byte encoding produces.
    /// </summary>
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, bool oddPadding)
    {
        if (bytes.Length < 16)
            return false;

        var padding = 0;
        var other = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != 0)
                continue;
            if (index % 2 == (oddPadding ? 1 : 0))
                padding++;
            else
                other++;
        }

        return padding > bytes.Length / 4 && other == 0;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            var following = bytes[index] switch
            {
                < 0x80 => 0,
                >= 0xC2 and <= 0xDF => 1,
                >= 0xE0 and <= 0xEF => 2,
                >= 0xF0 and <= 0xF4 => 3,
                _ => -1
            };

            if (following < 0)
                return false;

            // A sniff buffer usually ends inside a multi-byte sequence, and a
            // truncated tail is not evidence of anything.
            if (following > 0 && index + following >= bytes.Length)
                return true;

            for (var offset = 1; offset <= following; offset++)
                if ((bytes[index + offset] & 0xC0) != 0x80)
                    return false;

            index += following + 1;
        }

        return true;
    }
}
