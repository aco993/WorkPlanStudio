using System.Text;

namespace WorkPlanStudio.Services.Import;

/// <summary>
/// The single-byte code page a German Excel still writes when someone picks
/// "CSV (comma delimited)" on a machine that is not set to UTF-8.
/// </summary>
/// <remarks>
/// Implemented here rather than taken from <c>System.Text.Encoding.CodePages</c>:
/// that package exists to carry the full NLS code-page tables, and taking a
/// dependency on it to decode thirty-two characters would enlarge a WebAssembly
/// payload every visitor downloads. Windows-1252 is Latin-1 except in the C1
/// range, so the whole table is the array below and everything else is the
/// identity mapping — which is also why a mis-detected Windows-1252 file still
/// reads correctly as long as it is plain ASCII.
/// </remarks>
internal sealed class Windows1252Encoding : Encoding
{
    /// <summary>
    /// Code points for bytes 0x80–0x9F, the only range where Windows-1252 and
    /// Latin-1 disagree. The five holes are undefined in the code page and decode
    /// to U+FFFD, which is what makes a wrongly guessed encoding visible on screen
    /// instead of silently wrong.
    /// </summary>
    private static readonly char[] ControlRange =
    [
        '\u20AC', '\uFFFD', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\uFFFD', '\u017D', '\uFFFD',
        '\uFFFD', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\uFFFD', '\u017E', '\u0178'
    ];

    public static Windows1252Encoding Instance { get; } = new();

    private Windows1252Encoding() : base(1252) { }

    public override string EncodingName => "Windows-1252";

    public override int GetByteCount(char[] chars, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(chars);
        return count;
    }

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(bytes);

        for (var i = 0; i < charCount; i++)
            bytes[byteIndex + i] = ToByte(chars[charIndex + i]);

        return charCount;
    }

    public override int GetCharCount(byte[] bytes, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return count;
    }

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(chars);

        for (var i = 0; i < byteCount; i++)
        {
            var value = bytes[byteIndex + i];
            chars[charIndex + i] = value is >= 0x80 and <= 0x9F ? ControlRange[value - 0x80] : (char)value;
        }

        return byteCount;
    }

    public override int GetMaxByteCount(int charCount) => charCount;

    public override int GetMaxCharCount(int byteCount) => byteCount;

    private static byte ToByte(char value)
    {
        if (value < '\u0080' || value is >= '\u00A0' and <= '\u00FF')
            return (byte)value;

        var index = Array.IndexOf(ControlRange, value);
        return index >= 0 ? (byte)(0x80 + index) : (byte)'?';
    }
}
