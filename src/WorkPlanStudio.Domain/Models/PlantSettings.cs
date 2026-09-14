using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Models;

/// <summary>
/// The plant-wide working-time settings: which state's holidays apply and which
/// of the Arbeitszeitgesetz options the plant uses. One row; the app reads it
/// on every scheduling run and turns it into <see cref="WorkingTimeRules"/>.
/// </summary>
public class PlantSettings
{
    /// <summary>The single row always has id 1.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Two-letter state code, e.g. "NW". Decides the public holidays.</summary>
    public string State { get; set; } = GermanState.NW.ToString();

    /// <summary>Also close on days that are holidays only in parts of the state.</summary>
    public bool IncludePartialHolidays { get; set; }

    /// <summary>§3 sentence 2: use the 10-hour day with averaging.</summary>
    public bool AllowExtendedDay { get; set; } = true;

    /// <summary>
    /// §3 sentence 2: the reference period the werktäglich average is taken over.
    /// The subsection offers six calendar months <i>or</i> 24 weeks and the plant
    /// has to say which it uses, because the two disagree at the boundaries —
    /// 24 weeks is always 168 days, six calendar months is 181 to 184.
    /// </summary>
    public AveragingWindow AveragingWindow { get; set; } = AveragingWindow.TwentyFourWeeks;

    /// <summary>§6 (2): use the 10-hour night shift with averaging.</summary>
    public bool AllowExtendedNight { get; set; }

    /// <summary>§10: the plant falls under a Sunday-work exception.</summary>
    public bool SundayWorkAllowed { get; set; }

    /// <summary>§10: the plant falls under a holiday-work exception.</summary>
    public bool HolidayWorkAllowed { get; set; }

    /// <summary>
    /// §11 (1): how many weeks the crew rota runs before it repeats. A plant that
    /// works Sundays under a §10 exception leaves a crew every <c>n</c>-th Sunday
    /// free; without the number, the only honest reading of a Sunday-staffed
    /// weekly pattern is that the same crew works every Sunday of the year.
    /// </summary>
    public int SundayRotationWeeks { get; set; } = 1;

    /// <summary>§9 (2): hours the Sunday rest window is shifted, 0..6.</summary>
    public int SundayBoundaryShiftHours { get; set; }

    /// <summary>
    /// §5 (2): the sector that entitles the plant to shorten the rest to 10 h.
    /// Outside the sectors the subsection names there is no such entitlement at
    /// all, which is why <see cref="MinimumRestHours"/> is only allowed to reach
    /// 10 once this is set — in the form, in the validator and in a CHECK
    /// constraint, because a permission that is only hidden is still granted.
    /// </summary>
    public RestExceptionSector RestExceptionSector { get; set; } = RestExceptionSector.None;

    /// <summary>§5: hours of rest between two working days, 10 or 11.</summary>
    public int MinimumRestHours { get; set; } = 11;

    public DateTime ModifiedUtc { get; set; }

    /// <summary>The rules these settings describe.</summary>
    /// <remarks>
    /// The clamps are the last line of a defence that starts in the form and runs
    /// through the validator and the database. They exist because a row can also
    /// arrive from an import or from a server, and the safe reading of a
    /// shortened rest with no sector behind it is the statutory 11 h of §5 (1) —
    /// not the shortening.
    /// </remarks>
    public WorkingTimeRules ToRules()
    {
        var sector = Enum.IsDefined(RestExceptionSector) ? RestExceptionSector : WorkingTime.RestExceptionSector.None;
        var window = Enum.IsDefined(AveragingWindow) ? AveragingWindow : WorkingTime.AveragingWindow.TwentyFourWeeks;
        int restHours = sector == WorkingTime.RestExceptionSector.None ? 11 : Math.Clamp(MinimumRestHours, 10, 11);

        return WorkingTimeRules.Statutory with
        {
            State = Enum.TryParse<GermanState>(State, out var state) ? state : GermanState.NW,
            IncludePartialHolidays = IncludePartialHolidays,
            AllowExtendedDay = AllowExtendedDay,
            AveragingWindow = window,
            AllowExtendedNight = AllowExtendedNight,
            SundayWorkAllowed = SundayWorkAllowed,
            HolidayWorkAllowed = HolidayWorkAllowed,
            SundayRotationWeeks = Math.Clamp(SundayRotationWeeks, 1, 52),
            SundayBoundaryShift = TimeSpan.FromHours(Math.Clamp(SundayBoundaryShiftHours, 0, 6)),
            RestExceptionSector = sector,
            MinimumRest = TimeSpan.FromHours(restHours)
        };
    }

    /// <summary>A copy, so a form can edit without touching the loaded entity.</summary>
    public PlantSettings Clone() => (PlantSettings)MemberwiseClone();
}
