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
/// A one-off closed interval <c>[Start, End)</c> in plant-local wall-clock time —
/// the exception a repeating shift pattern cannot express. See
/// <see cref="PlantTime"/> for what "wall clock" commits the caller to.
/// </summary>
/// <param name="Start">First closed moment.</param>
/// <param name="End">First moment work may resume.</param>
/// <param name="Kind">Why.</param>
/// <param name="Label">Free text shown in the UI, e.g. "Sommerurlaub".</param>
public sealed record AbsencePeriod(DateTime Start, DateTime End, AbsenceKind Kind, string Label = "")
{
    /// <summary>The same interval read as wall clock, both ends stamped alike.</summary>
    public AbsencePeriod AsWallClock() => this with { Start = PlantTime.Wall(Start), End = PlantTime.Wall(End) };

    /// <summary>Throws unless the interval is ordered and both ends belong to the same world.</summary>
    public void Validate()
    {
        // Two different kinds on one interval is not a stylistic problem: the
        // subtraction that produces the duration ignores the kind, so the record
        // and its own JSON disagree by the local offset.
        if (Start.Kind != End.Kind)
            throw new ArgumentException(
                $"Absence '{Label}' mixes {Start.Kind} and {End.Kind}; both ends must be the same kind of time.", nameof(Start));
        if (End <= Start)
            throw new ArgumentException($"Absence '{Label}' must end after it starts.");
        if (Label.Length > 80)
            throw new ArgumentException("An absence label is at most 80 characters.");
    }
}
