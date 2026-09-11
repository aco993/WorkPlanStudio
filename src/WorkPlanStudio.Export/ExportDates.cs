using System.Globalization;

namespace WorkPlanStudio.Export;

/// <summary>
/// How an exported document writes a moment.
/// <para>
/// Both shipped languages render a purely numeric short date, and they disagree
/// about it: <c>11/09/2026</c> is 11 September to a British reader and 9
/// November to an American one, and <c>1.6.</c> is 1 June to a German one and
/// nothing at all to an English one. An export is printed, mailed and read
/// against a paper travelling card by someone who did not produce it, so a date
/// that can be read two ways is worse here than on screen. Every date therefore
/// carries an abbreviated or full month name in English, and the German
/// ordinal form in German - and a German export reads as German from the title
/// to the last axis tick.
/// </para>
/// <para>
/// This deliberately mirrors <c>WorkPlanStudio.Services.Format</c> in the app.
/// The duplication is the price of keeping this library free of any reference
/// to the app; a test in the web suite asserts that the two still agree, so the
/// copy cannot drift unnoticed.
/// </para>
/// </summary>
public static class ExportDates
{
    /// <summary>A full date with a month name: "11 Sep 2026" / "11. September 2026".</summary>
    public static string Date(DateTime moment, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return moment.ToString(DatePattern(culture), culture);
    }

    /// <summary>
    /// A time of day on the 24-hour clock in both languages. A production plan is
    /// read against machine logs and shift rosters, which never use am/pm.
    /// </summary>
    public static string Time(DateTime moment, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return moment.ToString("HH:mm", culture);
    }

    /// <summary>A date and a time: "11 Sep 2026 14:30" / "11. September 2026 14:30".</summary>
    public static string DateTime(DateTime moment, CultureInfo culture) =>
        Date(moment, culture) + " " + Time(moment, culture);

    /// <summary>
    /// A compact day for a chart axis or a dense column: "Fri 11 Sep" /
    /// "Fr 11.9.". The weekday leads because on an axis it is what a reader
    /// scans for, and because it is what makes the German form unambiguous.
    /// </summary>
    public static string DayMonth(DateTime moment, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return moment.ToString(DayMonthPattern(culture), culture);
    }

    /// <summary>A day and a time, for an axis tick inside a day: "Fri 11 Sep 06:00" / "Fr 11.9. 06:00".</summary>
    public static string DayMonthTime(DateTime moment, CultureInfo culture) =>
        DayMonth(moment, culture) + " " + Time(moment, culture);

    /// <summary>
    /// German writes the day as an ordinal ("11."), English does not ("11"). The
    /// test is the culture's own date separator rather than its name, so a third
    /// culture added later lands on the right branch without a code change.
    /// </summary>
    private static string DatePattern(CultureInfo culture) =>
        UsesOrdinalDay(culture) ? "d. MMMM yyyy" : "d MMM yyyy";

    private static string DayMonthPattern(CultureInfo culture) =>
        UsesOrdinalDay(culture) ? "ddd d.M." : "ddd d MMM";

    private static bool UsesOrdinalDay(CultureInfo culture) =>
        culture.DateTimeFormat.DateSeparator == ".";
}
