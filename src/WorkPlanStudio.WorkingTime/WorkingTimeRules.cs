namespace WorkPlanStudio.WorkingTime;

/// <summary>
/// The rules of the Arbeitszeitgesetz that a shift plan is checked and cut
/// against. Each value carries the section it comes from so a UI can explain
/// <i>why</i> a machine is idle, not just that it is.
/// </summary>
public enum WorkingTimeRuleId
{
    /// <summary>§3 ArbZG — the working day is capped at 8 h, or 10 h if averaged back to 8 h within 24 weeks.</summary>
    MaxDailyWorkingTime,

    /// <summary>§4 ArbZG — 30 min of break after 6 h, 45 min after 9 h, in pieces of at least 15 min; never more than 6 h without one.</summary>
    Breaks,

    /// <summary>§5 ArbZG — 11 h of uninterrupted rest between two working days of the same crew.</summary>
    RestPeriod,

    /// <summary>§6 ArbZG — night work (more than 2 h between 23:00 and 06:00) is capped at 8 h, or 10 h if averaged within a month.</summary>
    NightWork,

    /// <summary>§9 ArbZG — no work on Sundays from 00:00 to 24:00; §9 (2) lets multi-shift plants move that window by up to 6 h.</summary>
    SundayRest,

    /// <summary>§9 ArbZG — no work on public holidays; which days those are is set by the plant's state.</summary>
    HolidayRest,

    /// <summary>§11 ArbZG — at least 15 Sundays a year must stay free even where Sunday work is permitted.</summary>
    FreeSundays
}

/// <summary>Metadata for one rule: where it comes from in the law.</summary>
/// <param name="Id">The rule.</param>
/// <param name="LegalReference">The section, e.g. <c>§ 3 ArbZG</c>.</param>
public sealed record WorkingTimeRuleInfo(WorkingTimeRuleId Id, string LegalReference);

/// <summary>
/// Every knob of the working-time model, with the statutory defaults. All
/// durations are wall-clock; the model has no time zone and no daylight-saving
/// transitions — it describes a plant's local day.
/// </summary>
public sealed record WorkingTimeRules
{
    /// <summary>The state whose holidays apply.</summary>
    public GermanState State { get; init; } = GermanState.NW;

    /// <summary>Also close on days that are holidays only in parts of the state.</summary>
    public bool IncludePartialHolidays { get; init; }

    /// <summary>§3 sentence 1: the ordinary daily cap. Statutory value 8 h.</summary>
    public TimeSpan MaxDailyWorkingTime { get; init; } = TimeSpan.FromHours(8);

    /// <summary>§3 sentence 2: the extended cap when the average is brought back within the reference period. Statutory value 10 h.</summary>
    public TimeSpan ExtendedDailyWorkingTime { get; init; } = TimeSpan.FromHours(10);

    /// <summary>Whether the plant uses the §3 sentence 2 extension (averaging over 24 weeks / 6 months).</summary>
    public bool AllowExtendedDay { get; init; } = true;

    /// <summary>§4: break owed once the working day exceeds 6 h. Statutory 30 min.</summary>
    public TimeSpan BreakAfterSixHours { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>§4: break owed once the working day exceeds 9 h. Statutory 45 min.</summary>
    public TimeSpan BreakAfterNineHours { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>§4: the smallest piece a break may be split into. Statutory 15 min.</summary>
    public TimeSpan MinimumBreakPiece { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>§4: the longest stretch of work without any break. Statutory 6 h.</summary>
    public TimeSpan MaxWorkWithoutBreak { get; init; } = TimeSpan.FromHours(6);

    /// <summary>§5: uninterrupted rest between working days. Statutory 11 h (10 h in a few named sectors).</summary>
    public TimeSpan MinimumRest { get; init; } = TimeSpan.FromHours(11);

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

    /// <summary>Whether the plant uses the §6 (2) extension.</summary>
    public bool AllowExtendedNight { get; init; }

    /// <summary>§9 (1) forbids Sunday work; §10 lists the exceptions (continuous processes, energy, hospitals…). False = Sundays are closed.</summary>
    public bool SundayWorkAllowed { get; init; }

    /// <summary>Same for public holidays.</summary>
    public bool HolidayWorkAllowed { get; init; }

    /// <summary>§9 (2): multi-shift plants may shift the Sunday/holiday rest window by up to 6 h, 0..6 h.</summary>
    public TimeSpan SundayBoundaryShift { get; init; } = TimeSpan.Zero;

    /// <summary>§11 (1): Sundays per year that must stay free. Statutory 15.</summary>
    public int MinimumFreeSundaysPerYear { get; init; } = 15;

    /// <summary>The statutory defaults for a plant in North Rhine-Westphalia.</summary>
    public static WorkingTimeRules Statutory => new();

    /// <summary>Where every rule comes from, for the UI's explanations.</summary>
    public static IReadOnlyList<WorkingTimeRuleInfo> Catalog { get; } =
    [
        new(WorkingTimeRuleId.MaxDailyWorkingTime, "§ 3 ArbZG"),
        new(WorkingTimeRuleId.Breaks, "§ 4 ArbZG"),
        new(WorkingTimeRuleId.RestPeriod, "§ 5 ArbZG"),
        new(WorkingTimeRuleId.NightWork, "§ 6 ArbZG"),
        new(WorkingTimeRuleId.SundayRest, "§ 9 ArbZG"),
        new(WorkingTimeRuleId.HolidayRest, "§ 9 ArbZG"),
        new(WorkingTimeRuleId.FreeSundays, "§ 11 ArbZG")
    ];

    /// <summary>The daily cap in force: extended when the plant uses §3 sentence 2.</summary>
    public TimeSpan DailyCap => AllowExtendedDay ? ExtendedDailyWorkingTime : MaxDailyWorkingTime;

    /// <summary>The night cap in force.</summary>
    public TimeSpan NightCap => AllowExtendedNight ? ExtendedNightWorkingTime : MaxNightWorkingTime;

    /// <summary>The break owed for a working day of <paramref name="workingTime"/> (net of breaks).</summary>
    public TimeSpan BreakOwed(TimeSpan workingTime)
    {
        if (workingTime > TimeSpan.FromHours(9)) return BreakAfterNineHours;
        if (workingTime > TimeSpan.FromHours(6)) return BreakAfterSixHours;
        return TimeSpan.Zero;
    }

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
        Positive(MinimumRest, nameof(MinimumRest));
        Positive(MaxNightWorkingTime, nameof(MaxNightWorkingTime));
        Positive(ExtendedNightWorkingTime, nameof(ExtendedNightWorkingTime));

        if (ExtendedDailyWorkingTime < MaxDailyWorkingTime)
            throw new ArgumentOutOfRangeException(nameof(ExtendedDailyWorkingTime), "The extended cap cannot be below the ordinary cap.");
        if (BreakAfterSixHours < TimeSpan.Zero || BreakAfterNineHours < BreakAfterSixHours)
            throw new ArgumentOutOfRangeException(nameof(BreakAfterNineHours), "Breaks must be non-negative and non-decreasing.");
        if (MinimumBreakPiece <= TimeSpan.Zero || MinimumBreakPiece > BreakAfterSixHours)
            throw new ArgumentOutOfRangeException(nameof(MinimumBreakPiece), "A break piece must be positive and no longer than the shorter break.");
        if (SundayBoundaryShift < TimeSpan.Zero || SundayBoundaryShift > TimeSpan.FromHours(6))
            throw new ArgumentOutOfRangeException(nameof(SundayBoundaryShift), "§ 9 (2) allows at most 6 h.");
        if (MinimumFreeSundaysPerYear is < 0 or > 52)
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeSundaysPerYear));
        if (!Enum.IsDefined(State))
            throw new ArgumentOutOfRangeException(nameof(State));
    }
}
