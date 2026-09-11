namespace WorkPlanStudio.WorkingTime;

/// <summary>
/// The rules of the Arbeitszeitgesetz that a shift plan is checked and cut
/// against. Each value carries the section it comes from so a UI can explain
/// <i>why</i> a machine is idle, not just that it is.
/// </summary>
public enum WorkingTimeRuleId
{
    /// <summary>
    /// §3 ArbZG — 8 h a day, or 10 h where the six-month / 24-week average stays
    /// at 8 h. The cap is applied per crew and calendar day; the average is
    /// evaluated separately by <see cref="WorkingTimeline.Evaluate"/>.
    /// </summary>
    MaxDailyWorkingTime,

    /// <summary>§4 ArbZG — 30 min of break after 6 h, 45 min after 9 h, in pieces of at least 15 min; never more than 6 h without one.</summary>
    Breaks,

    /// <summary>§5 ArbZG — 11 h of uninterrupted rest between two working days of the same crew.</summary>
    RestPeriod,

    /// <summary>
    /// §5 (2) ArbZG — a rest shortened to 10 h is lawful only in the sectors the
    /// subsection names, and only if another rest of the same crew is extended to
    /// 12 h within a month. Raised when the shortening is taken and the
    /// compensation is not in the pattern.
    /// </summary>
    RestCompensation,

    /// <summary>§6 ArbZG — night work (more than 2 h between 23:00 and 06:00) is capped at 8 h, or 10 h where the one-month average stays at 8 h.</summary>
    NightWork,

    /// <summary>§9 ArbZG — no work on Sundays from 00:00 to 24:00; §9 (2) lets multi-shift plants move that window by up to 6 h either way.</summary>
    SundayRest,

    /// <summary>§9 ArbZG — no work on public holidays; which days those are is set by the plant's state.</summary>
    HolidayRest,

    /// <summary>
    /// §9 (2) ArbZG — the moved Sunday boundary is reserved for plants running a
    /// regular day <i>and</i> night shift. Raised when a pattern that is not
    /// multi-shift claims it.
    /// </summary>
    MultiShiftRequirement,

    /// <summary>§11 ArbZG — at least 15 Sundays a year must stay free even where Sunday work is permitted.</summary>
    FreeSundays,

    /// <summary>
    /// §11 (2) / (3) ArbZG — work on a Sunday earns a replacement rest day within
    /// two weeks, work on a public holiday one within eight weeks. Raised as a
    /// reminder of the obligation the Sunday or holiday permission carries with it.
    /// </summary>
    ReplacementRestDay
}

/// <summary>
/// Which reference period the plant averages the extended working day over.
/// §3 sentence 2 offers the choice; the two are not interchangeable at the
/// boundaries, because six calendar months is 181 to 184 days and 24 weeks is
/// always 168.
/// </summary>
public enum AveragingWindow
{
    /// <summary>24 weeks — 168 days, the shorter and the simpler of the two.</summary>
    TwentyFourWeeks,

    /// <summary>Six calendar months, counted from each candidate start date.</summary>
    SixCalendarMonths
}

/// <summary>
/// The sectors §5 (2) ArbZG names as allowed to shorten the rest period to 10 h.
/// Anything else — a machine shop included — may not, which is why the default
/// says so.
/// </summary>
public enum RestExceptionSector
{
    /// <summary>No exception: the full 11 h of §5 (1) apply.</summary>
    None,

    /// <summary>Krankenhäuser und andere Einrichtungen zur Behandlung und Pflege.</summary>
    HealthCare,

    /// <summary>Gaststätten und andere Einrichtungen zur Bewirtung und Beherbergung.</summary>
    Hospitality,

    /// <summary>Verkehrsbetriebe.</summary>
    Transport,

    /// <summary>Rundfunk.</summary>
    Broadcasting,

    /// <summary>Landwirtschaft und Tierhaltung.</summary>
    Agriculture
}

/// <summary>Metadata for one rule: where it comes from in the law.</summary>
/// <param name="Id">The rule.</param>
/// <param name="LegalReference">The section, e.g. <c>§ 3 ArbZG</c>.</param>
public sealed record WorkingTimeRuleInfo(WorkingTimeRuleId Id, string LegalReference);

/// <summary>
/// Every knob of the working-time model, with the statutory defaults.
/// <para>
/// All times of day are plant-local wall clock and the model itself has no time
/// zone — see <see cref="PlantTime"/> for what that costs and how the two days a
/// year on which a wall-clock difference is not elapsed time are handled.
/// </para>
/// </summary>
public sealed record WorkingTimeRules
{
    /// <summary>The state whose holidays apply.</summary>
    public GermanState State { get; init; } = GermanState.NW;

    /// <summary>Also close on days that are holidays only in parts of the state.</summary>
    public bool IncludePartialHolidays { get; init; }

    /// <summary>§3 sentence 1: the ordinary daily cap, and the average the extension has to come back to. Statutory value 8 h.</summary>
    public TimeSpan MaxDailyWorkingTime { get; init; } = TimeSpan.FromHours(8);

    /// <summary>§3 sentence 2: the extended cap when the average is brought back within the reference period. Statutory value 10 h.</summary>
    public TimeSpan ExtendedDailyWorkingTime { get; init; } = TimeSpan.FromHours(10);

    /// <summary>
    /// Whether the plant takes the §3 sentence 2 extension. Taking it is an
    /// obligation, not a checkbox: <see cref="WorkingTimeline.Evaluate"/> computes
    /// the werktäglich average over <see cref="AveragingWindow"/> and reports the
    /// date the plan first breaches it.
    /// </summary>
    public bool AllowExtendedDay { get; init; } = true;

    /// <summary>The reference period §3 sentence 2 and §6 (2) are averaged over.</summary>
    public AveragingWindow AveragingWindow { get; init; } = AveragingWindow.TwentyFourWeeks;

    /// <summary>§4: break owed once the working day exceeds <see cref="BreakThresholdShort"/>. Statutory 30 min.</summary>
    public TimeSpan BreakAfterSixHours { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>§4: break owed once the working day exceeds <see cref="BreakThresholdLong"/>. Statutory 45 min.</summary>
    public TimeSpan BreakAfterNineHours { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>
    /// §4 sentence 1, first tier: the working time above which a break is owed at
    /// all. Statutory 6 h.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MaxWorkWithoutBreak"/> on purpose. The two numbers
    /// coincide at 6 h in the statute but say different things, and a works
    /// agreement that tightens the uninterrupted stretch to 4 h must not thereby
    /// invent a break for a five-hour shift.
    /// </remarks>
    public TimeSpan BreakThresholdShort { get; init; } = TimeSpan.FromHours(6);

    /// <summary>§4 sentence 1, second tier: the working time above which the longer break is owed. Statutory 9 h.</summary>
    public TimeSpan BreakThresholdLong { get; init; } = TimeSpan.FromHours(9);

    /// <summary>§4: the smallest piece a break may be split into. Statutory 15 min.</summary>
    public TimeSpan MinimumBreakPiece { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>§4 sentence 3: the longest stretch of work without any break. Statutory 6 h. Drives where breaks are placed, not whether one is owed.</summary>
    public TimeSpan MaxWorkWithoutBreak { get; init; } = TimeSpan.FromHours(6);

    /// <summary>§5 (1): uninterrupted rest between working days. Statutory 11 h.</summary>
    public TimeSpan MinimumRest { get; init; } = TimeSpan.FromHours(11);

    /// <summary>
    /// §5 (2): the sector that lets the plant shorten the rest to 10 h. A plant
    /// outside the listed sectors has no such permission, which is why the
    /// default is <see cref="RestExceptionSector.None"/>; taking the shortening
    /// anyway raises <see cref="WorkingTimeRuleId.RestCompensation"/>.
    /// </summary>
    public RestExceptionSector RestExceptionSector { get; init; } = RestExceptionSector.None;

    /// <summary>§5 (2): the length every compensating rest has to reach. Statutory 12 h.</summary>
    public TimeSpan CompensatingRest { get; init; } = TimeSpan.FromHours(12);

    /// <summary>§2 (3): the night period. Statutory 23:00–06:00.</summary>
    public TimeOnly NightStart { get; init; } = new(23, 0);

    /// <summary>§2 (3): end of the night period.</summary>
    public TimeOnly NightEnd { get; init; } = new(6, 0);

    /// <summary>§2 (4): a shift is night work once it covers more than this much of the night period. Statutory 2 h.</summary>
    public TimeSpan NightWorkThreshold { get; init; } = TimeSpan.FromHours(2);

    /// <summary>§6 (2): daily cap for night work. Statutory 8 h.</summary>
    public TimeSpan MaxNightWorkingTime { get; init; } = TimeSpan.FromHours(8);

    /// <summary>§6 (2): extended cap for night work when averaged within a month. Statutory 10 h.</summary>
    public TimeSpan ExtendedNightWorkingTime { get; init; } = TimeSpan.FromHours(10);

    /// <summary>Whether the plant uses the §6 (2) extension. Averaged and reported like <see cref="AllowExtendedDay"/>.</summary>
    public bool AllowExtendedNight { get; init; }

    /// <summary>§9 (1) forbids Sunday work; §10 lists the exceptions (continuous processes, energy, hospitals…). False = Sundays are closed.</summary>
    public bool SundayWorkAllowed { get; init; }

    /// <summary>Same for public holidays.</summary>
    public bool HolidayWorkAllowed { get; init; }

    /// <summary>
    /// §9 (2): multi-shift plants with a regular day and night shift may move the
    /// start and end of the Sunday and holiday rest by up to six hours
    /// <i>forward or back</i> — <c>-6 h</c> runs the closed day from Saturday
    /// 18:00, <c>+6 h</c> from Sunday 06:00.
    /// </summary>
    public TimeSpan SundayBoundaryShift { get; init; } = TimeSpan.Zero;

    /// <summary>§11 (1): Sundays per year that must stay free. Statutory 15.</summary>
    public int MinimumFreeSundaysPerYear { get; init; } = 15;

    /// <summary>
    /// How many weeks the crew rota runs before it repeats. A weekly shift
    /// pattern on its own says only <i>whether</i> a crew works Sundays, and a
    /// crew that works every Sunday of the year has none free — which would flag
    /// every lawful §10 plant. Declaring the rota (four crews taking one Sunday in
    /// four is <c>4</c>) is what turns that into a real count.
    /// </summary>
    public int SundayRotationWeeks { get; init; } = 1;

    /// <summary>§11 (3): the window a replacement rest day for Sunday work has to fall in. Statutory two weeks.</summary>
    public TimeSpan SundayReplacementRestWindow { get; init; } = TimeSpan.FromDays(14);

    /// <summary>§11 (2): the window a replacement rest day for holiday work has to fall in. Statutory eight weeks.</summary>
    public TimeSpan HolidayReplacementRestWindow { get; init; } = TimeSpan.FromDays(56);

    /// <summary>The statutory defaults for a plant in North Rhine-Westphalia.</summary>
    public static WorkingTimeRules Statutory => new();

    /// <summary>Where every rule comes from, for the UI's explanations.</summary>
    public static IReadOnlyList<WorkingTimeRuleInfo> Catalog { get; } =
    [
        new(WorkingTimeRuleId.MaxDailyWorkingTime, "§ 3 ArbZG"),
        new(WorkingTimeRuleId.Breaks, "§ 4 ArbZG"),
        new(WorkingTimeRuleId.RestPeriod, "§ 5 ArbZG"),
        new(WorkingTimeRuleId.RestCompensation, "§ 5 (2) ArbZG"),
        new(WorkingTimeRuleId.NightWork, "§ 6 ArbZG"),
        new(WorkingTimeRuleId.SundayRest, "§ 9 ArbZG"),
        new(WorkingTimeRuleId.HolidayRest, "§ 9 ArbZG"),
        new(WorkingTimeRuleId.MultiShiftRequirement, "§ 9 (2) ArbZG"),
        new(WorkingTimeRuleId.FreeSundays, "§ 11 ArbZG"),
        new(WorkingTimeRuleId.ReplacementRestDay, "§ 11 (2), (3) ArbZG")
    ];

    /// <summary>The daily cap in force: extended when the plant uses §3 sentence 2.</summary>
    public TimeSpan DailyCap => AllowExtendedDay ? ExtendedDailyWorkingTime : MaxDailyWorkingTime;

    /// <summary>The night cap in force.</summary>
    public TimeSpan NightCap => AllowExtendedNight ? ExtendedNightWorkingTime : MaxNightWorkingTime;

    /// <summary>Length of the night period, midnight crossing included.</summary>
    public TimeSpan NightLength
    {
        get
        {
            var span = NightEnd.ToTimeSpan() - NightStart.ToTimeSpan();
            return span <= TimeSpan.Zero ? span + TimeSpan.FromDays(1) : span;
        }
    }

    /// <summary>The break owed for a working day of <paramref name="workingTime"/> (net of breaks).</summary>
    public TimeSpan BreakOwed(TimeSpan workingTime)
    {
        if (workingTime > BreakThresholdLong) return BreakAfterNineHours;
        if (workingTime > BreakThresholdShort) return BreakAfterSixHours;
        return TimeSpan.Zero;
    }

    /// <summary>
    /// The working time a shift of <paramref name="gross"/> presence leaves: the
    /// longest <c>w ≤ gross</c> for which the break <see cref="BreakOwed"/> demands
    /// still fits in what is left over.
    /// </summary>
    /// <remarks>
    /// Solving it this way rather than testing the threshold on <paramref name="gross"/>
    /// is what makes the function monotonic. A presence of 6 h 00 m 01 s owes no
    /// 30-minute break — there is no 6 h 01 m of <i>working time</i> in it, only
    /// 6 h and a second of idle presence — so it must not yield less working time
    /// than a presence of 6 h. §4 counts Arbeitszeit, and Arbeitszeit is what is
    /// left once the break is out.
    /// </remarks>
    public TimeSpan NetWorkingTime(TimeSpan gross)
    {
        // Tier 0: no break owed at all, so at most the first threshold.
        var best = gross < BreakThresholdShort ? gross : BreakThresholdShort;

        // Tier 1: over the first threshold, up to the second, costs the short break.
        if (gross - BreakAfterSixHours > BreakThresholdShort)
        {
            var candidate = gross - BreakAfterSixHours;
            if (candidate > BreakThresholdLong)
                candidate = BreakThresholdLong;
            if (candidate > best)
                best = candidate;
        }

        // Tier 2: over the second threshold costs the long break.
        if (gross - BreakAfterNineHours > BreakThresholdLong)
        {
            var candidate = gross - BreakAfterNineHours;
            if (candidate > best)
                best = candidate;
        }

        return best;
    }

    /// <summary>The shortest presence that yields <paramref name="net"/> of working time — the net plus the break it owes.</summary>
    public TimeSpan GrossForNet(TimeSpan net) => net + BreakOwed(net);

    /// <summary>Throws unless the values are internally consistent.</summary>
    public void Validate()
    {
        static void Positive(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero || value > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(name, value, "Must be between 0 and 24 h.");
        }

        Positive(MaxDailyWorkingTime, nameof(MaxDailyWorkingTime));
        Positive(ExtendedDailyWorkingTime, nameof(ExtendedDailyWorkingTime));
        Positive(MaxWorkWithoutBreak, nameof(MaxWorkWithoutBreak));
        Positive(BreakThresholdShort, nameof(BreakThresholdShort));
        Positive(BreakThresholdLong, nameof(BreakThresholdLong));
        Positive(MinimumRest, nameof(MinimumRest));
        Positive(CompensatingRest, nameof(CompensatingRest));
        Positive(MaxNightWorkingTime, nameof(MaxNightWorkingTime));
        Positive(ExtendedNightWorkingTime, nameof(ExtendedNightWorkingTime));

        if (ExtendedDailyWorkingTime < MaxDailyWorkingTime)
            throw new ArgumentOutOfRangeException(nameof(ExtendedDailyWorkingTime), "The extended cap cannot be below the ordinary cap.");
        if (ExtendedNightWorkingTime < MaxNightWorkingTime)
            throw new ArgumentOutOfRangeException(nameof(ExtendedNightWorkingTime), "The extended night cap cannot be below the ordinary one.");
        if (BreakThresholdLong < BreakThresholdShort)
            throw new ArgumentOutOfRangeException(nameof(BreakThresholdLong), "The second break tier cannot start below the first.");
        if (BreakAfterSixHours < TimeSpan.Zero || BreakAfterNineHours < BreakAfterSixHours)
            throw new ArgumentOutOfRangeException(nameof(BreakAfterNineHours), "Breaks must be non-negative and non-decreasing.");
        if (BreakAfterNineHours > TimeSpan.FromHours(4))
            throw new ArgumentOutOfRangeException(nameof(BreakAfterNineHours), BreakAfterNineHours, "A break longer than four hours is a second shift, not a break.");
        if (MinimumBreakPiece <= TimeSpan.Zero || MinimumBreakPiece > BreakAfterSixHours)
            throw new ArgumentOutOfRangeException(nameof(MinimumBreakPiece), "A break piece must be positive and no longer than the shorter break.");

        // §5 (1) is the floor; §5 (2) reaches 10 h and no lower, in any sector.
        if (MinimumRest < TimeSpan.FromHours(10))
            throw new ArgumentOutOfRangeException(nameof(MinimumRest), MinimumRest, "§ 5 never allows less than 10 h of rest.");
        if (MinimumRest + DailyCap > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(MinimumRest), MinimumRest, "The rest and the daily cap together must fit inside a day.");
        if (CompensatingRest < MinimumRest)
            throw new ArgumentOutOfRangeException(nameof(CompensatingRest), "A compensating rest cannot be shorter than the ordinary one.");

        // §2 (3): an empty night window would make every hour of the clock night,
        // and every shift night work.
        if (NightStart == NightEnd)
            throw new ArgumentOutOfRangeException(nameof(NightEnd), NightEnd, "The night period must be non-empty.");
        if (NightWorkThreshold <= TimeSpan.Zero || NightWorkThreshold > NightLength)
            throw new ArgumentOutOfRangeException(nameof(NightWorkThreshold), NightWorkThreshold, "The night-work threshold must lie inside the night period.");

        var shift = SundayBoundaryShift < TimeSpan.Zero ? -SundayBoundaryShift : SundayBoundaryShift;
        if (shift > TimeSpan.FromHours(6))
            throw new ArgumentOutOfRangeException(nameof(SundayBoundaryShift), SundayBoundaryShift, "§ 9 (2) allows at most 6 h, forward or back.");

        // A year has 52 or 53 Sundays, so 53 is the ceiling a plant can be asked for.
        if (MinimumFreeSundaysPerYear is < 0 or > 53)
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeSundaysPerYear));
        if (SundayRotationWeeks is < 1 or > 52)
            throw new ArgumentOutOfRangeException(nameof(SundayRotationWeeks), SundayRotationWeeks, "A rota runs between 1 and 52 weeks.");
        if (SundayReplacementRestWindow <= TimeSpan.Zero || HolidayReplacementRestWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SundayReplacementRestWindow), "A replacement-rest window must be positive.");

        if (!Enum.IsDefined(State))
            throw new ArgumentOutOfRangeException(nameof(State), State, "Not one of the sixteen states.");
        if (!Enum.IsDefined(AveragingWindow))
            throw new ArgumentOutOfRangeException(nameof(AveragingWindow), AveragingWindow, "Unknown reference period.");
        if (!Enum.IsDefined(RestExceptionSector))
            throw new ArgumentOutOfRangeException(nameof(RestExceptionSector), RestExceptionSector, "Unknown sector.");
    }
}
