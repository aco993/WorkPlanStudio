namespace WorkPlanStudio.Services;

/// <summary>
/// Small, culture-aware formatting helpers. All numeric formatting uses the
/// current UI culture, so values appear as "1,234.5" in English and "1.234,5"
/// in German automatically.
/// </summary>
public static class Format
{
    public static string Hours(decimal minutes) => (minutes / 60m).ToString("N1") + " h";

    public static string Minutes(decimal minutes) => minutes.ToString("N1") + " min";

    public static string Euro(decimal value) => value.ToString("N0") + " €";

    public static string Number(decimal value) => value.ToString("N0");
}
