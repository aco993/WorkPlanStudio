namespace WorkPlanStudio.WorkingTime;

/// <summary>Why a work center is unavailable for a stretch of calendar time.</summary>
public enum AbsenceKind
{
    /// <summary>The crew is on leave.</summary>
    Vacation,

    /// <summary>Planned maintenance or retooling.</summary>
    Maintenance,

    /// <summary>Unplanned: illness, breakdown.</summary>
    Unplanned,

    /// <summary>Anything else; see the label.</summary>
    Other
}

/// <summary>
/// A one-off closed interval <c>[Start, End)</c> in wall-clock time — the
/// exception a repeating shift pattern cannot express.
/// </summary>
/// <param name="Start">First closed moment.</param>
/// <param name="End">First moment work may resume.</param>
/// <param name="Kind">Why.</param>
/// <param name="Label">Free text shown in the UI, e.g. "Sommerurlaub".</param>
public sealed record AbsencePeriod(DateTime Start, DateTime End, AbsenceKind Kind, string Label = "")
{
    /// <summary>Throws unless the interval is ordered.</summary>
    public void Validate()
    {
        if (End <= Start)
            throw new ArgumentException($"Absence '{Label}' must end after it starts.");
        if (Label.Length > 80)
            throw new ArgumentException("An absence label is at most 80 characters.");
    }
}
