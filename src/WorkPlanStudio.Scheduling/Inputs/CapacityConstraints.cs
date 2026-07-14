namespace WorkPlanStudio.Scheduling;

/// <summary>A half-open interval [start, end) in which a work center may process work.</summary>
public sealed record CapacityWindow(long StartSeconds, long EndSeconds)
{
    /// <summary>Ensures the interval is ordered and non-negative.</summary>
    public void Validate()
    {
        if (StartSeconds < 0 || EndSeconds <= StartSeconds)
            throw new ArgumentOutOfRangeException(nameof(EndSeconds), "Capacity window must be a positive interval.");
    }
}

/// <summary>Sequence-dependent setup time between two operation families.</summary>
public sealed record SetupDuration(string FromFamily, string ToFamily, long DurationSeconds)
{
    /// <summary>Ensures the transition is usable by the scheduling core.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FromFamily) || string.IsNullOrWhiteSpace(ToFamily) || DurationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "Setup transition is invalid.");
    }
}
