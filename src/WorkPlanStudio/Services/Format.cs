using System.Globalization;

namespace WorkPlanStudio.Services;

/// <summary>
/// The single place the application turns a number, a duration, a money amount
/// or a moment into text. Everything here reads <see cref="CultureInfo.CurrentUICulture"/>,
/// so a value appears as "1,234.5" in English and "1.234,5" in German without a
/// call site having to know which language is on.
/// <para>
/// Dates deserve a word. Both shipped cultures write a purely numeric short date,
/// and they disagree about it: <c>6/4/2026</c> is 6 April to an American reader
/// and — read as <c>4.6.2026</c> — 4 June to a German one. Since the app is read
/// in both languages, and often by people checking a shop-floor date against a
/// paper travelling card, every date it renders carries an abbreviated month name
/// instead. Both cultures then read the same day.
/// </para>
/// </summary>
public static class Format
{
    private static CultureInfo Culture => CultureInfo.CurrentUICulture;

    /// <summary>
    /// A no-break space between a value and its unit. "7,5" and "h" are one reading;
    /// letting a line break fall between them turns a duration into two fragments.
    /// </summary>
    public const string NoBreak = "\u00a0";

    /// <summary>A duration given in minutes, as hours: "7.5 h" / "7,5 h".</summary>
    public static string Hours(decimal minutes) =>
        (minutes / 60m).ToString("N1", Culture) + NoBreak + "h";

    /// <summary>A duration in minutes: "90.0 min" / "90,0 min".</summary>
    public static string Minutes(decimal minutes) =>
        minutes.ToString("N1", Culture) + NoBreak + "min";

    /// <summary>
    /// A duration as the shop floor writes it: "45 min", "2 h", "2 h 30 min".
    /// Whole hours drop the minutes rather than showing "2 h 00 min".
    /// </summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.FromHours(1))
            return ((int)value.TotalMinutes).ToString("0", Culture) + NoBreak + "min";

        var wholeHours = (int)value.TotalHours;
        return value.Minutes == 0
            ? wholeHours.ToString("0", Culture) + NoBreak + "h"
            : wholeHours.ToString("0", Culture) + NoBreak + "h " + value.Minutes.ToString("00", Culture) + NoBreak + "min";
    }

    /// <summary>
    /// A money amount in euro. The currency format comes from the culture — symbol
    /// before the amount in English, after it in German — but the symbol itself is
    /// forced to euro, because the plant bills in euro whichever language its
    /// planner reads. Cents are kept: a lot cost is not a round number.
    /// </summary>
    public static string Euro(decimal value) =>
        value.ToString("C2", EuroFormat());

    /// <summary>A plain integer count: "1,234" / "1.234".</summary>
    public static string Number(decimal value) => value.ToString("N0", Culture);

    /// <summary>The same with a fixed number of decimals: "5.3" / "5,3".</summary>
    /// <param name="value">The number.</param>
    /// <param name="decimals">Digits after the separator.</param>
    public static string Number(decimal value, int decimals) =>
        value.ToString("N" + decimals.ToString(System.Globalization.CultureInfo.InvariantCulture), Culture);

    /// <summary>
    /// A short elapsed time: milliseconds below a second, otherwise seconds with
    /// one decimal. A measurement printed to five decimals reads as a claim about
    /// precision the clock does not have.
    /// </summary>
    /// <param name="elapsed">How long something took.</param>
    public static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 1
            ? Number((decimal)elapsed.TotalMilliseconds) + " ms"
            : Number((decimal)elapsed.TotalSeconds, 1) + " s";

    /// <summary>
    /// A rate in 0..1 as a percentage, rounded to whole points. The culture decides
    /// whether a space precedes the sign, which is why this is not string concatenation.
    /// </summary>
    public static string Percent(double rate) => rate.ToString("P0", Culture);

    /// <summary>A full date with a month name: "4 Jun 2026" / "4. Juni 2026".</summary>
    public static string Date(DateTime moment) => moment.ToString(DatePattern(), Culture);

    /// <summary>A full date with a month name: "4 Jun 2026" / "4. Juni 2026".</summary>
    public static string Date(DateOnly date) => date.ToString(DatePattern(), Culture);

    /// <summary>A date and a time of day: "4 Jun 2026 06:00" / "4. Juni 2026 06:00".</summary>
    public static string DateTime(DateTime moment) => Date(moment) + " " + Time(moment);

    /// <summary>
    /// A time of day on the 24-hour clock in both languages. A production plan is
    /// read against machine logs and shift rosters, which never use am/pm.
    /// </summary>
    public static string Time(DateTime moment) => moment.ToString("HH:mm", Culture);

    /// <summary>
    /// A compact day for a chart axis or a dense table: "Di 2.6." / "Tue 2 Jun".
    /// The weekday is what the reader actually scans for, so it leads.
    /// </summary>
    public static string DayMonth(DateTime moment) => moment.ToString(DayMonthPattern(), Culture);

    /// <summary>A compact day for a chart axis or a dense table: "Di 2.6." / "Tue 2 Jun".</summary>
    public static string DayMonth(DateOnly date) => date.ToString(DayMonthPattern(), Culture);

    /// <summary>A day and a time, for a Gantt bar's start and end: "Di 2.6. 06:00".</summary>
    public static string Stamp(DateTime moment) => DayMonth(moment) + " " + Time(moment);

    /// <summary>
    /// German writes the day as an ordinal ("4."), English does not ("4"). The test
    /// is the culture's own date separator rather than its name, so a third culture
    /// added later lands on the right branch without a code change.
    /// </summary>
    private static string DatePattern() => UsesOrdinalDay() ? "d. MMMM yyyy" : "d MMM yyyy";

    private static string DayMonthPattern() => UsesOrdinalDay() ? "ddd d.M." : "ddd d MMM";

    private static bool UsesOrdinalDay() => Culture.DateTimeFormat.DateSeparator == ".";

    /// <summary>
    /// The current culture's number formatting with the currency pinned to euro.
    /// Cloned rather than mutated: <see cref="CultureInfo.CurrentUICulture"/> is
    /// shared, and writing to its format info would change every other caller.
    /// </summary>
    private static NumberFormatInfo EuroFormat()
    {
        var format = (NumberFormatInfo)Culture.NumberFormat.Clone();
        format.CurrencySymbol = "€";
        return format;
    }
}
