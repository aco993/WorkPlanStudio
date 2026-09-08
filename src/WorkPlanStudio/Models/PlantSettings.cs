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

    /// <summary>§6 (2): use the 10-hour night shift with averaging.</summary>
    public bool AllowExtendedNight { get; set; }

    /// <summary>§10: the plant falls under a Sunday-work exception.</summary>
    public bool SundayWorkAllowed { get; set; }

    /// <summary>§10: the plant falls under a holiday-work exception.</summary>
    public bool HolidayWorkAllowed { get; set; }

    /// <summary>§9 (2): hours the Sunday rest window is shifted, 0..6.</summary>
    public int SundayBoundaryShiftHours { get; set; }

    /// <summary>§5: hours of rest between two working days, 10 or 11.</summary>
    public int MinimumRestHours { get; set; } = 11;

    public DateTime ModifiedUtc { get; set; }

    /// <summary>The rules these settings describe.</summary>
    public WorkingTimeRules ToRules() => WorkingTimeRules.Statutory with
    {
        State = Enum.TryParse<GermanState>(State, out var state) ? state : GermanState.NW,
        IncludePartialHolidays = IncludePartialHolidays,
        AllowExtendedDay = AllowExtendedDay,
        AllowExtendedNight = AllowExtendedNight,
        SundayWorkAllowed = SundayWorkAllowed,
        HolidayWorkAllowed = HolidayWorkAllowed,
        SundayBoundaryShift = TimeSpan.FromHours(Math.Clamp(SundayBoundaryShiftHours, 0, 6)),
        MinimumRest = TimeSpan.FromHours(Math.Clamp(MinimumRestHours, 10, 11))
    };

    /// <summary>A copy, so a form can edit without touching the loaded entity.</summary>
    public PlantSettings Clone() => (PlantSettings)MemberwiseClone();
}
