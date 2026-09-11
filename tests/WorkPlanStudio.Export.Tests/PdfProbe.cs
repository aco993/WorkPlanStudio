using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkPlanStudio.Export.Tests;

/// <summary>
/// A deliberately small PDF parser, written for the tests only: it finds the
/// cross-reference table, reads the byte offsets back and pulls out the content
/// streams.
/// <para>
/// Everything a PDF reader does starts at the cross-reference table, so this is
/// where a hand-written writer goes wrong in a way that no amount of "it looks
/// right in one viewer" would catch. Parsing the offsets back and checking each
/// one lands on the object header it claims is the test that proves the file is
/// navigable rather than merely plausible.
/// </para>
/// </summary>
internal sealed class PdfProbe
{
    private readonly byte[] _bytes;

    public PdfProbe(byte[] bytes)
    {
        _bytes = bytes;
        Text = Encoding.Latin1.GetString(bytes);
        Offsets = ReadCrossReferenceTable();
    }

    /// <summary>The whole file as Latin-1 text, which for the structural parts is the file verbatim.</summary>
    public string Text { get; }

    /// <summary>Byte offset of each object, indexed from object 1.</summary>
    public IReadOnlyList<long> Offsets { get; }

    /// <summary>The <c>/Count</c> of the page tree.</summary>
    public int PageCount =>
        int.Parse(Regex.Match(Text, @"/Type\s*/Pages\s*/Kids\s*\[[^\]]*\]\s*/Count\s+(?<count>\d+)").Groups["count"].Value,
            CultureInfo.InvariantCulture);

    /// <summary>Every content stream, decoded as Latin-1 - the writer does not compress them.</summary>
    public IReadOnlyList<string> ContentStreams() =>
        [.. StreamRanges().Select(range => Encoding.Latin1.GetString(_bytes, range.Start, range.Length))];

    /// <summary>Where each content stream starts and how long its dictionary claims it is.</summary>
    public IReadOnlyList<(int Start, int Length)> StreamRanges()
    {
        var ranges = new List<(int, int)>();
        foreach (Match match in Regex.Matches(Text, @"<< /Length (?<length>\d+) >>\nstream\n"))
            ranges.Add((match.Index + match.Length, int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture)));

        return ranges;
    }

    /// <summary>The object header the cross-reference table points at, for example "7 0 obj".</summary>
    public string ObjectHeaderAt(int objectNumber)
    {
        long offset = Offsets[objectNumber - 1];
        int end = Array.IndexOf(_bytes, (byte)'\n', (int)offset);
        return Encoding.Latin1.GetString(_bytes, (int)offset, (int)(end - offset));
    }

    private IReadOnlyList<long> ReadCrossReferenceTable()
    {
        var startxref = Regex.Match(Text, @"startxref\s+(?<offset>\d+)\s+%%EOF");
        Assert.True(startxref.Success, "the trailer has no startxref");

        int tableStart = int.Parse(startxref.Groups["offset"].Value, CultureInfo.InvariantCulture);
        var table = Text[tableStart..];
        var header = Regex.Match(table, @"^xref\r?\n0 (?<count>\d+)\r?\n");
        Assert.True(header.Success, "startxref does not point at an xref table");

        var entries = Regex.Matches(table, @"(?<offset>\d{10}) \d{5} (?<kind>[nf]) ");
        var offsets = new List<long>();
        foreach (Match entry in entries)
        {
            if (entry.Groups["kind"].Value == "f")
                continue;
            offsets.Add(long.Parse(entry.Groups["offset"].Value, CultureInfo.InvariantCulture));
        }

        return offsets;
    }
}
