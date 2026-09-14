using System.Collections.Concurrent;

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
/// German public holidays per state and year. The dates are computed rather than
/// tabled — the movable feasts derive from Easter — but the <i>law</i> is tabled,
/// because it changes: Reformationstag became a holiday in four northern states
/// in 2018, Buß- und Bettag stopped being one outside Sachsen in 1995, and the
/// Frauentag arrived in Berlin in 2019 and in Mecklenburg-Vorpommern in 2023. A
/// calendar that applied today's statute to 2017 would close the wrong plants on
/// the wrong days, so every entry carries the years it was in force.
/// <para>
/// The supported range starts at <see cref="MinYear"/> — the year the sixteen
/// states came to exist in their present form — and ends at <see cref="MaxYear"/>,
/// beyond which projecting today's Feiertagsgesetze would be invention rather
/// than computation. A year outside it, or an undefined
/// <see cref="GermanState"/>, is refused rather than answered plausibly.
/// </para>
/// <para>
/// One-off holidays proclaimed for a single anniversary <i>are</i> included where
/// they are known (the 2017 nationwide Reformationstag, Berlin's 8 May in 2020
/// and 2025). A future one-off obviously cannot be.
/// </para>
/// </summary>
public static class GermanHolidays
{
    /// <summary>First year the tables cover: German reunification.</summary>
    public const int MinYear = 1990;

    /// <summary>
    /// Last year the tables cover. Far enough ahead for any plan, near enough
    /// that the answer is still today's law carried forward rather than a guess.
    /// </summary>
    public const int MaxYear = 2200;

    private static readonly GermanState[] AllStates = Enum.GetValues<GermanState>();
    private static readonly GermanState[] Epiphany = [GermanState.BW, GermanState.BY, GermanState.ST];
    private static readonly GermanState[] CorpusChristiStates = [GermanState.BW, GermanState.BY, GermanState.HE, GermanState.NW, GermanState.RP, GermanState.SL];
    private static readonly GermanState[] CorpusChristiPartial = [GermanState.SN, GermanState.TH];
    private static readonly GermanState[] AllSaintsStates = [GermanState.BW, GermanState.BY, GermanState.NW, GermanState.RP, GermanState.SL];
    private static readonly GermanState[] ReformationAlways = [GermanState.BB, GermanState.MV, GermanState.SN, GermanState.ST, GermanState.TH];
    private static readonly GermanState[] ReformationFrom2018 = [GermanState.HB, GermanState.HH, GermanState.NI, GermanState.SH];

    /// <summary>The eleven states of the Federal Republic before 3 October 1990, Berlin included.</summary>
    private static readonly GermanState[] OldFederalRepublic =
    [
        GermanState.BW, GermanState.BY, GermanState.BE, GermanState.HB, GermanState.HH,
        GermanState.HE, GermanState.NI, GermanState.NW, GermanState.RP, GermanState.SL, GermanState.SH
    ];

    private static readonly GermanState[] Saarland = [GermanState.SL];
    private static readonly GermanState[] Bavaria = [GermanState.BY];
    private static readonly GermanState[] Saxony = [GermanState.SN];
    private static readonly GermanState[] Thuringia = [GermanState.TH];
    private static readonly GermanState[] Berlin = [GermanState.BE];
    private static readonly GermanState[] Brandenburg = [GermanState.BB];
    private static readonly GermanState[] MecklenburgVorpommern = [GermanState.MV];

    /// <summary>
    /// Holidays are a pure function of (year, state, includePartial) and the app
    /// asks for the same year repeatedly — once per timeline build, once per page
    /// render. The cache is cleared wholesale once it grows past a bound rather
    /// than evicting by age: the working set is a handful of years and a single
    /// state, so a bounded dictionary never actually reaches the bound in
    /// practice, and clearing keeps it lock-free.
    /// </summary>
    private static readonly ConcurrentDictionary<(int Year, GermanState State, bool IncludePartial), IReadOnlyList<PublicHoliday>> Cache = new();

    private const int CacheBound = 2048;

    /// <summary>
    /// Easter Sunday for a Gregorian year (the anonymous Gregorian algorithm,
    /// also known as Meeus/Jones/Butcher). Exact for every Gregorian year — the
    /// astronomy does not change, so this accepts a far wider range than the
    /// statutes in <see cref="ForYear"/> do.
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
    /// <exception cref="ArgumentOutOfRangeException">
    /// The year lies outside <see cref="MinYear"/>..<see cref="MaxYear"/>, or the
    /// state is not a defined <see cref="GermanState"/>.
    /// </exception>
    public static IReadOnlyList<PublicHoliday> ForYear(int year, GermanState state, bool includePartial = false)
    {
        if (year is < MinYear or > MaxYear)
            throw new ArgumentOutOfRangeException(
                nameof(year), year, $"The holiday tables cover {MinYear} to {MaxYear}; outside that the law is not known.");
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Not one of the sixteen states.");

        var key = (year, state, includePartial);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var computed = Compute(year, state, includePartial);
        if (Cache.Count >= CacheBound)
            Cache.Clear();
        Cache[key] = computed;
        return computed;
    }

    private static IReadOnlyList<PublicHoliday> Compute(int year, GermanState state, bool includePartial)
    {
        var easter = EasterSunday(year);
        var list = new List<PublicHoliday>();

        void Add(DateOnly date, string key, string de, string en, HolidayScope scope) =>
            list.Add(new PublicHoliday(date, key, de, en, scope));

        void Nationwide(DateOnly date, string key, string de, string en) =>
            Add(date, key, de, en, HolidayScope.Nationwide);

        // `from`/`to` are the first and last year the entry was in force. They are
        // what makes this a calendar of the law rather than of today's law.
        void InStates(DateOnly date, string key, string de, string en, int from, int to, GermanState[] states)
        {
            if (year >= from && year <= to && Array.IndexOf(states, state) >= 0)
                Add(date, key, de, en, HolidayScope.State);
        }

        void PartialIn(DateOnly date, string key, string de, string en, GermanState[] states)
        {
            if (includePartial && Array.IndexOf(states, state) >= 0)
                Add(date, key, de, en, HolidayScope.Partial);
        }

        Nationwide(new DateOnly(year, 1, 1), "NewYear", "Neujahr", "New Year's Day");
        InStates(new DateOnly(year, 1, 6), "Epiphany", "Heilige Drei Könige", "Epiphany", MinYear, MaxYear, Epiphany);

        // §1 Abs. 1 BbgFTG lists Ostersonntag and Pfingstsonntag; Brandenburg is
        // the only state that does. It changes no capacity — §9 closes Sundays
        // anyway — but it is what the statute says and what a holiday list shows.
        InStates(easter, "EasterSunday", "Ostersonntag", "Easter Sunday", MinYear, MaxYear, Brandenburg);
        InStates(easter.AddDays(49), "WhitSunday", "Pfingstsonntag", "Whit Sunday", MinYear, MaxYear, Brandenburg);

        // Berlin from 2019, Mecklenburg-Vorpommern from 2023.
        InStates(new DateOnly(year, 3, 8), "WomensDay", "Internationaler Frauentag", "International Women's Day", 2019, MaxYear, Berlin);
        InStates(new DateOnly(year, 3, 8), "WomensDay", "Internationaler Frauentag", "International Women's Day", 2023, MaxYear, MecklenburgVorpommern);

        Nationwide(easter.AddDays(-2), "GoodFriday", "Karfreitag", "Good Friday");
        Nationwide(easter.AddDays(1), "EasterMonday", "Ostermontag", "Easter Monday");
        Nationwide(new DateOnly(year, 5, 1), "LabourDay", "Tag der Arbeit", "Labour Day");

        // Berlin proclaimed 8 May a one-off holiday for the 75th and the 80th
        // anniversary of the end of the war. Both were single years.
        InStates(new DateOnly(year, 5, 8), "LiberationDay", "Tag der Befreiung", "Liberation Day", 2020, 2020, Berlin);
        InStates(new DateOnly(year, 5, 8), "LiberationDay", "Tag der Befreiung", "Liberation Day", 2025, 2025, Berlin);

        // The last Tag der deutschen Einheit on 17 June: the Einigungsvertrag
        // moved the day to 3 October with effect from 3 October 1990, so 1990 is
        // the only year in range that has both.
        InStates(new DateOnly(year, 6, 17), "GermanUnityJune17", "Tag der deutschen Einheit", "Day of German Unity", 1990, 1990, OldFederalRepublic);

        Nationwide(easter.AddDays(39), "AscensionDay", "Christi Himmelfahrt", "Ascension Day");
        Nationwide(easter.AddDays(50), "WhitMonday", "Pfingstmontag", "Whit Monday");
        InStates(easter.AddDays(60), "CorpusChristi", "Fronleichnam", "Corpus Christi", MinYear, MaxYear, CorpusChristiStates);
        PartialIn(easter.AddDays(60), "CorpusChristi", "Fronleichnam", "Corpus Christi", CorpusChristiPartial);
        PartialIn(new DateOnly(year, 8, 8), "AugsburgPeaceFestival", "Augsburger Friedensfest", "Augsburg Peace Festival", Bavaria);
        InStates(new DateOnly(year, 8, 15), "Assumption", "Mariä Himmelfahrt", "Assumption Day", MinYear, MaxYear, Saarland);
        PartialIn(new DateOnly(year, 8, 15), "Assumption", "Mariä Himmelfahrt", "Assumption Day", Bavaria);
        InStates(new DateOnly(year, 9, 20), "ChildrensDay", "Weltkindertag", "World Children's Day", 2019, MaxYear, Thuringia);
        Nationwide(new DateOnly(year, 10, 3), "GermanUnity", "Tag der Deutschen Einheit", "German Unity Day");

        // 2017 was a one-off nationwide Reformationstag for the 500th anniversary;
        // the five eastern states had it anyway, the four northern ones gained it
        // permanently only from 2018.
        if (year == 2017)
            InStates(new DateOnly(2017, 10, 31), "ReformationDay", "Reformationstag", "Reformation Day", 2017, 2017, AllStates);
        else
        {
            InStates(new DateOnly(year, 10, 31), "ReformationDay", "Reformationstag", "Reformation Day", MinYear, MaxYear, ReformationAlways);
            InStates(new DateOnly(year, 10, 31), "ReformationDay", "Reformationstag", "Reformation Day", 2018, MaxYear, ReformationFrom2018);
        }

        InStates(new DateOnly(year, 11, 1), "AllSaints", "Allerheiligen", "All Saints' Day", MinYear, MaxYear, AllSaintsStates);

        // Nationwide until 1994; the 1995 Pflegeversicherung reform traded it for
        // the employer's share of the new contribution in every state but Sachsen,
        // which kept the day and raised the employee's share instead.
        InStates(RepentanceDay(year), "RepentanceDay", "Buß- und Bettag", "Day of Repentance and Prayer", MinYear, 1994, AllStates);
        InStates(RepentanceDay(year), "RepentanceDay", "Buß- und Bettag", "Day of Repentance and Prayer", 1995, MaxYear, Saxony);

        Nationwide(new DateOnly(year, 12, 25), "ChristmasDay", "1. Weihnachtstag", "Christmas Day");
        Nationwide(new DateOnly(year, 12, 26), "BoxingDay", "2. Weihnachtstag", "Boxing Day");

        list.Sort((x, y) => x.Date.CompareTo(y.Date));
        return list;
    }

    /// <summary>
    /// Every holiday between two dates (inclusive), across year boundaries. Both
    /// years must lie in <see cref="MinYear"/>..<see cref="MaxYear"/>, which is
    /// also what bounds the cost: the loop can never run more years than the
    /// tables cover.
    /// </summary>
    public static IReadOnlyList<PublicHoliday> Between(DateOnly from, DateOnly to, GermanState state, bool includePartial = false)
    {
        if (to < from)
            throw new ArgumentException("The range end must not precede its start.", nameof(to));
        if (from.Year < MinYear)
            throw new ArgumentOutOfRangeException(nameof(from), from, $"The holiday tables start in {MinYear}.");
        if (to.Year > MaxYear)
            throw new ArgumentOutOfRangeException(nameof(to), to, $"The holiday tables end in {MaxYear}.");
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Not one of the sixteen states.");

        var result = new List<PublicHoliday>();
        for (int year = from.Year; year <= to.Year; year++)
            foreach (var holiday in ForYear(year, state, includePartial))
                if (holiday.Date >= from && holiday.Date <= to)
                    result.Add(holiday);
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
