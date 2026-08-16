namespace WorkPlanStudio.Scheduling;

/// <summary>
/// A half-open interval <c>[start, end)</c> in which a work center may process
/// work — a shift, or a working day.
/// <para>
/// Windows are positions on the same abstract work-time axis as everything else
/// in the engine, not wall-clock times. Keeping calendars out of
/// <see cref="DateTimeOffset"/> is what keeps the core free of time zones and
/// daylight-saving transitions while still expressing "this machine is not
/// available at night".
/// </para>
/// </summary>
public sealed record CapacityWindow(long StartSeconds, long EndSeconds)
{
    /// <summary>Length of the window in seconds.</summary>
    public long DurationSeconds => EndSeconds - StartSeconds;

    /// <summary>Throws unless the interval is ordered and non-negative.</summary>
    public void Validate()
    {
        if (StartSeconds < 0 || EndSeconds <= StartSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(EndSeconds),
                $"Capacity window [{StartSeconds}, {EndSeconds}) must be a positive interval starting at or after 0.");
    }
}

/// <summary>
/// Sequence-dependent setup time: how long a work center needs to change over
/// from one operation family to another. A missing transition costs nothing, and
/// running the same family twice in a row costs nothing.
/// <para>
/// This is what makes the *order* of operations on a machine matter beyond
/// simple queueing — running all the steel parts together and then all the
/// aluminium ones is cheaper than alternating.
/// </para>
/// </summary>
public sealed record SetupDuration(string FromFamily, string ToFamily, long DurationSeconds)
{
    /// <summary>Throws unless both families are named and the duration is non-negative.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FromFamily) || string.IsNullOrWhiteSpace(ToFamily))
            throw new ArgumentOutOfRangeException(nameof(FromFamily), "Setup transition needs a source and target family.");
        if (DurationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "Setup transition cannot be negative.");
    }
}
