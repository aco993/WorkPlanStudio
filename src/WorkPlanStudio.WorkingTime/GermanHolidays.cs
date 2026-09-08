namespace WorkPlanStudio.WorkingTime;

/// <summary>How widely a holiday applies.</summary>
public enum HolidayScope
{
    /// <summary>A legal holiday in all sixteen states.</summary>
    Nationwide,

    /// <summary>A legal holiday in the whole of the given state.</summary>
    State,

    /// <summary>
    /// A legal holiday only in parts of the state (e.g. Mariä Himmelfahrt in
    /// Bavaria's Catholic-majority municipalities). Included only on request,
    /// because whether it applies depends on the plant's municipality.
    /// </summary>
    Partial
}

/// <summary>A public holiday on a specific date, with its scope in the state it was computed for.</summary>
/// <param name="Date">The calendar date.</param>
/// <param name="Key">Stable identifier used for localisation, e.g. <c>GoodFriday</c>.</param>
/// <param name="NameDe">The official German name.</param>
/// <param name="NameEn">The customary English name.</param>
/// <param name="Scope">Nationwide, state-wide or only in parts of the state.</param>
public sealed record PublicHoliday(DateOnly Date, string Key, string NameDe, string NameEn, HolidayScope Scope);

/// <summary>
/// German public holidays per state and year, computed rather than tabled so
/// every year works. The nine nationwide days plus the state-specific ones set
/// by each state's Feiertagsgesetz; the movable feasts derive from Easter.
/// </summary>
public static class GermanHolidays
{
    /// <summary>
    /// Easter Sunday for a Gregorian year (the anonymous Gregorian algorithm,
    /// also known as Meeus/Jones/Butcher). Exact for every Gregorian year.
    /// </summary>
    public static DateOnly EasterSunday(int year)
    {
        if (year is < 1583 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(year), year, "Gregorian years only.");

        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>
    /// Every legal holiday of <paramref name="year"/> in <paramref name="state"/>,
    /// sorted by date. <paramref name="includePartial"/> adds the days that are
    /// holidays only in parts of the state.
    /// </summary>
    public static IReadOnlyList<PublicHoliday> ForYear(int year, GermanState state, bool includePartial = false)
    {
        var easter = EasterSunday(year);
        var list = new List<PublicHoliday>();

        void Add(DateOnly date, string key, string de, string en, HolidayScope scope) =>
            list.Add(new PublicHoliday(date, key, de, en, scope));

        void Nationwide(DateOnly date, string key, string de, string en) =>
            Add(date, key, de, en, HolidayScope.Nationwide);

        void InStates(DateOnly date, string key, string de, string en, params GermanState[] states)
        {
            if (states.Contains(state))
                Add(date, key, de, en, HolidayScope.State);
        }

        void PartialIn(DateOnly date, string key, string de, string en, params GermanState[] states)
        {
            if (includePartial && states.Contains(state))
                Add(date, key, de, en, HolidayScope.Partial);
        }

        Nationwide(new DateOnly(year, 1, 1), "NewYear", "Neujahr", "New Year's Day");
        InStates(new DateOnly(year, 1, 6), "Epiphany", "Heilige Drei Könige", "Epiphany", GermanState.BW, GermanState.BY, GermanState.ST);
        InStates(new DateOnly(year, 3, 8), "WomensDay", "Internationaler Frauentag", "International Women's Day", GermanState.BE, GermanState.MV);
        Nationwide(easter.AddDays(-2), "GoodFriday", "Karfreitag", "Good Friday");
        Nationwide(easter.AddDays(1), "EasterMonday", "Ostermontag", "Easter Monday");
        Nationwide(new DateOnly(year, 5, 1), "LabourDay", "Tag der Arbeit", "Labour Day");
        Nationwide(easter.AddDays(39), "AscensionDay", "Christi Himmelfahrt", "Ascension Day");
        Nationwide(easter.AddDays(50), "WhitMonday", "Pfingstmontag", "Whit Monday");
        InStates(easter.AddDays(60), "CorpusChristi", "Fronleichnam", "Corpus Christi",
            GermanState.BW, GermanState.BY, GermanState.HE, GermanState.NW, GermanState.RP, GermanState.SL);
        PartialIn(easter.AddDays(60), "CorpusChristi", "Fronleichnam", "Corpus Christi", GermanState.SN, GermanState.TH);
        PartialIn(new DateOnly(year, 8, 8), "AugsburgPeaceFestival", "Augsburger Friedensfest", "Augsburg Peace Festival", GermanState.BY);
        InStates(new DateOnly(year, 8, 15), "Assumption", "Mariä Himmelfahrt", "Assumption Day", GermanState.SL);
        PartialIn(new DateOnly(year, 8, 15), "Assumption", "Mariä Himmelfahrt", "Assumption Day", GermanState.BY);
        InStates(new DateOnly(year, 9, 20), "ChildrensDay", "Weltkindertag", "World Children's Day", GermanState.TH);
        Nationwide(new DateOnly(year, 10, 3), "GermanUnity", "Tag der Deutschen Einheit", "German Unity Day");
        InStates(new DateOnly(year, 10, 31), "ReformationDay", "Reformationstag", "Reformation Day",
            GermanState.BB, GermanState.HB, GermanState.HH, GermanState.MV, GermanState.NI, GermanState.SN, GermanState.ST, GermanState.SH, GermanState.TH);
        InStates(new DateOnly(year, 11, 1), "AllSaints", "Allerheiligen", "All Saints' Day",
            GermanState.BW, GermanState.BY, GermanState.NW, GermanState.RP, GermanState.SL);
        InStates(RepentanceDay(year), "RepentanceDay", "Buß- und Bettag", "Day of Repentance and Prayer", GermanState.SN);
        Nationwide(new DateOnly(year, 12, 25), "ChristmasDay", "1. Weihnachtstag", "Christmas Day");
        Nationwide(new DateOnly(year, 12, 26), "BoxingDay", "2. Weihnachtstag", "Boxing Day");

        list.Sort((x, y) => x.Date.CompareTo(y.Date));
        return list;
    }

    /// <summary>Every holiday between two dates (inclusive), across year boundaries.</summary>
    public static IReadOnlyList<PublicHoliday> Between(DateOnly from, DateOnly to, GermanState state, bool includePartial = false)
    {
        if (to < from)
            throw new ArgumentException("The range end must not precede its start.", nameof(to));

        var result = new List<PublicHoliday>();
        for (int year = from.Year; year <= to.Year; year++)
            result.AddRange(ForYear(year, state, includePartial).Where(h => h.Date >= from && h.Date <= to));
        return result;
    }

    /// <summary>
    /// Buß- und Bettag: the Wednesday before 23 November, i.e. the last Wednesday
    /// before the last Sunday of the church year.
    /// </summary>
    public static DateOnly RepentanceDay(int year)
    {
        var date = new DateOnly(year, 11, 22);
        while (date.DayOfWeek != DayOfWeek.Wednesday)
            date = date.AddDays(-1);
        return date;
    }
}
