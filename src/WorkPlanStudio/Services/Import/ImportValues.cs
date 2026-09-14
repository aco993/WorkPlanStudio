using System.Globalization;

namespace WorkPlanStudio.Services.Import;

/// <summary>
/// Turns the text in a cell into a number, a date or a flag, under rules that are
/// written down rather than inherited from whatever culture the browser happens
/// to be set to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>CurrentCulture</c>.</b> The same file must import the same way
/// for everyone, and the person who exported it is rarely the person importing
/// it. Parsing <c>3,5</c> with the visitor's culture makes it three and a half in
/// Berlin and thirty-five in London, silently, on a field that drives cost. So
/// the rules below are fixed, and anything they cannot read is rejected by name
/// instead of being guessed at.
/// </para>
/// <para>
/// <b>Decimals.</b> A single separator is always the decimal point:
/// <c>1,5</c> and <c>1.5</c> both read as one and a half. Thousands separators
/// are only recognised when the value contains both kinds (<c>1.234,56</c>,
/// <c>1,234.56</c> — the last one wins as the decimal point) or repeats the same
/// one (<c>1.234.567</c>). The ambiguous middle case, <c>1,234</c>, is therefore
/// 1.234 and not 1234; a rule that resolved it the other way would silently
/// multiply an hourly rate by a thousand, which is the worse failure.
/// </para>
/// <para>
/// <b>Whole numbers.</b> A quantity has no fractional part, so there the rule is
/// reversed: a separator followed by exactly three digits is a thousands
/// separator and anything else is rejected. <c>1.234</c> is 1234 pieces and
/// <c>1.23</c> is refused rather than rounded.
/// </para>
/// <para>
/// <b>Dates.</b> Exactly two shapes are accepted — <c>31.12.2026</c> and
/// <c>2026-12-31</c>, each optionally followed by a time — because they are the
/// two that cannot be confused with each other. <c>01/02/2026</c> is refused: it
/// is the first of February in one convention and the second of January in the
/// other, and a due date six months wrong is worse than a due date missing.
/// </para>
/// </remarks>
public static class ImportValues
{
    private static readonly string[] DateFormats =
    [
        "dd.MM.yyyy", "d.M.yyyy",
        "dd.MM.yyyy HH:mm", "d.M.yyyy HH:mm",
        "dd.MM.yyyy HH:mm:ss", "d.M.yyyy HH:mm:ss",
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"
    ];

    private static readonly string[] TrueWords = ["true", "yes", "y", "1", "ja", "j", "x", "active", "aktiv"];

    private static readonly string[] FalseWords = ["false", "no", "n", "0", "nein", "inactive", "inaktiv"];

    /// <summary>The two date shapes, for the message that refuses everything else.</summary>
    public const string AcceptedDateFormats = "31.12.2026 / 2026-12-31";

    /// <summary>Trims a cell and collapses the spaces spreadsheets pad numbers with.</summary>
    public static string Clean(string? value) =>
        value is null
            ? ""
            : value.Replace('\u00A0', ' ').Replace('\u202F', ' ').Trim();

    /// <summary>A decimal under the rule documented on this class.</summary>
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0m;
        var cleaned = Clean(text).Replace(" ", "", StringComparison.Ordinal);
        if (cleaned.Length == 0)
            return false;

        var sign = "";
        if (cleaned[0] is '+' or '-')
        {
            sign = cleaned[0] == '-' ? "-" : "";
            cleaned = cleaned[1..];
        }

        var dots = cleaned.Count(c => c == '.');
        var commas = cleaned.Count(c => c == ',');

        string normalised;
        if (dots > 0 && commas > 0)
        {
            // Both present: whichever comes last is the decimal point.
            var decimalSeparator = cleaned.LastIndexOf('.') > cleaned.LastIndexOf(',') ? '.' : ',';
            var grouping = decimalSeparator == '.' ? ',' : '.';
            normalised = cleaned.Replace(grouping.ToString(), "", StringComparison.Ordinal)
                                .Replace(decimalSeparator, '.');
        }
        else if (dots > 1 || commas > 1)
        {
            // The same separator repeated can only be grouping.
            normalised = cleaned.Replace(".", "", StringComparison.Ordinal)
                                .Replace(",", "", StringComparison.Ordinal);
        }
        else
        {
            normalised = cleaned.Replace(',', '.');
        }

        return decimal.TryParse(
            sign + normalised,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>A whole number, where a separator can only be grouping.</summary>
    public static bool TryParseInt(string? text, out int value)
    {
        value = 0;
        var cleaned = Clean(text).Replace(" ", "", StringComparison.Ordinal);
        if (cleaned.Length == 0)
            return false;

        var negative = cleaned[0] == '-';
        if (cleaned[0] is '+' or '-')
            cleaned = cleaned[1..];

        if (!TryUngroup(cleaned, out var digits))
            return false;

        return int.TryParse(
            (negative ? "-" : "") + digits,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// Strips thousands separators from a whole number, or refuses it.
    /// </summary>
    /// <remarks>
    /// Accepts plain digits, or digit groups of exactly three separated by one
    /// consistently repeated <c>.</c> or <c>,</c>. That is what makes <c>1.234</c>
    /// 1234 pieces here while the decimal rule reads the same text as 1.234 — a
    /// quantity has no fractional part, so the separator cannot be a decimal
    /// point, and <c>1.23</c> is refused rather than silently rounded.
    /// </remarks>
    private static bool TryUngroup(string cleaned, out string digits)
    {
        digits = "";
        if (cleaned.Length == 0)
            return false;

        if (cleaned.All(char.IsAsciiDigit))
        {
            digits = cleaned;
            return true;
        }

        var separator = cleaned.FirstOrDefault(character => character is '.' or ',');
        var parts = cleaned.Split(separator);
        if (parts.Length < 2)
            return false;

        if (parts[0].Length is < 1 or > 3)
            return false;

        for (var index = 1; index < parts.Length; index++)
            if (parts[index].Length != 3)
                return false;

        if (!parts.All(part => part.All(char.IsAsciiDigit)))
            return false;

        digits = string.Concat(parts);
        return true;
    }

    /// <summary>
    /// A date in one of the two accepted shapes, as a plant-local wall-clock
    /// reading with no time zone attached — the one time model this app has.
    /// </summary>
    public static bool TryParseDate(string? text, out DateTime value)
    {
        var cleaned = Clean(text);
        return DateTime.TryParseExact(
            cleaned,
            DateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    /// <summary>A yes/no cell in either language, or the blank that means "use the default".</summary>
    public static bool TryParseBool(string? text, out bool value)
    {
        value = false;
        var cleaned = Clean(text).ToLowerInvariant();
        if (cleaned.Length == 0)
            return false;

        if (TrueWords.Contains(cleaned, StringComparer.Ordinal))
        {
            value = true;
            return true;
        }

        return FalseWords.Contains(cleaned, StringComparer.Ordinal);
    }
}
