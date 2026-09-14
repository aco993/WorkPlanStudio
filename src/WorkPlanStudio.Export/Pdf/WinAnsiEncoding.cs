namespace WorkPlanStudio.Export.Pdf;

/// <summary>
/// Maps text to the single-byte encoding the document declares on its fonts.
/// <para>
/// WinAnsiEncoding is Windows code page 1252. It is the widest single-byte
/// encoding a standard Type 1 font can be used with, and it covers everything
/// this application produces: German umlauts, the sharp s, the euro sign, the
/// en and em dashes and the middle dot the UI uses as a separator. Going beyond
/// it would mean embedding a font programme with a CMap, which is exactly the
/// dependency the writer exists to avoid - so a character outside the page is
/// replaced rather than silently dropped, and the replacement is visible.
/// </para>
/// </summary>
public static class WinAnsiEncoding
{
    /// <summary>Stands in for a character code page 1252 has no glyph for.</summary>
    public const byte Replacement = (byte)'?';

    private const char Delete = (char)0x7F;

    // Codes 0x80-0x9F are where CP1252 departs from Latin-1: typographic
    // punctuation and the euro sign sit in what ISO 8859-1 leaves as controls.
    private static readonly (char Character, byte Code)[] HighPunctuation =
    [
        ('€', 0x80), ('‚', 0x82), ('ƒ', 0x83), ('„', 0x84), ('…', 0x85),
        ('†', 0x86), ('‡', 0x87), ('ˆ', 0x88), ('‰', 0x89), ('Š', 0x8A),
        ('‹', 0x8B), ('Œ', 0x8C), ('Ž', 0x8E), ('‘', 0x91), ('’', 0x92),
        ('“', 0x93), ('”', 0x94), ('•', 0x95), ('–', 0x96), ('—', 0x97),
        ('˜', 0x98), ('™', 0x99), ('š', 0x9A), ('›', 0x9B), ('œ', 0x9C),
        ('ž', 0x9E), ('Ÿ', 0x9F)
    ];

    private static readonly Dictionary<char, byte> ByCharacter = BuildMap();

    private static Dictionary<char, byte> BuildMap()
    {
        var map = new Dictionary<char, byte>(256);

        // 0x20-0x7E and 0xA0-0xFF are Latin-1, where the code equals the code point.
        for (char character = ' '; character <= '~'; character++)
            map[character] = (byte)character;
        for (int code = 0xA0; code <= 0xFF; code++)
            map[(char)code] = (byte)code;

        foreach (var (character, code) in HighPunctuation)
            map[character] = code;

        return map;
    }

    /// <summary>True when <paramref name="character"/> has a glyph in the encoding.</summary>
    public static bool CanEncode(char character) => ByCharacter.ContainsKey(character);

    /// <summary>
    /// The byte for <paramref name="character"/>. Control characters become a
    /// space - a text-showing operator cannot break a line, so the alternative
    /// is an invisible glyph that silently swallows the separator - and anything
    /// else outside the code page becomes <see cref="Replacement"/>.
    /// </summary>
    public static byte Encode(char character)
    {
        if (character < ' ' || character == Delete)
            return (byte)' ';

        return ByCharacter.TryGetValue(character, out var code) ? code : Replacement;
    }

    /// <summary>
    /// Encodes <paramref name="text"/> and escapes it for a PDF literal string.
    /// Only three bytes need escaping - the parentheses that delimit a literal
    /// and the backslash that escapes them - because <see cref="Encode"/> has
    /// already removed every byte below a space.
    /// </summary>
    public static byte[] EncodeLiteral(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(text.Length + 8);
        foreach (var character in text)
        {
            var code = Encode(character);
            if (code is (byte)'(' or (byte)')' or (byte)'\\')
                bytes.Add((byte)'\\');
            bytes.Add(code);
        }

        return [.. bytes];
    }
}
