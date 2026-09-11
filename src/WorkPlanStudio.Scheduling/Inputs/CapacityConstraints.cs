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
/// An absolute closed interval <c>[start, end)</c> during which a work center
/// cannot process work, layered on top of its repeating calendar: a public
/// holiday, a maintenance stop, a crew on leave.
/// <para>
/// The repeating calendar (<see cref="MachineCapacity.AvailabilityWindows"/>)
/// expresses the <i>pattern</i> — a day shift, a five-day week. Blackouts are
/// the <i>exceptions</i> to that pattern, and unlike the pattern they are finite:
/// a schedule that runs past the last blackout is simply unconstrained by
/// exceptions from then on. Callers decide how far ahead to materialise them.
/// </para>
/// <para>
/// <paramref name="Tag"/> is an opaque label the caller can use to explain the
/// gap in a UI ("Fronleichnam", "Wartung"); the engine never interprets it.
/// </para>
/// <para>
/// A blackout is never bridged, and the test is against the operation's whole
/// wall span — the pauses it takes across breaks included, not only the seconds
/// it is actually busy. That is deliberate rather than an oversight: a
/// maintenance stop or a plant shutdown means the machine is physically
/// unavailable, so a half-finished part cannot sit in it across the stop and
/// resume afterwards. The cost of that reading is visible: a blackout covering
/// exactly a break the machine was closed for anyway still pushes the operation
/// to the next window, even though it removes no open seconds.
/// </para>
/// </summary>
public sealed record CapacityBlackout(long StartSeconds, long EndSeconds, string Tag)
{
    /// <summary>Length of the blackout in seconds.</summary>
    public long DurationSeconds => EndSeconds - StartSeconds;

    /// <summary>Throws unless the interval is ordered, non-negative and tagged.</summary>
    public void Validate()
    {
        if (StartSeconds < 0 || EndSeconds <= StartSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(EndSeconds),
                $"Blackout [{StartSeconds}, {EndSeconds}) must be a positive interval starting at or after 0.");
        if (string.IsNullOrWhiteSpace(Tag) || Tag.Length > 80)
            throw new ArgumentOutOfRangeException(nameof(Tag), "A blackout needs a tag of at most 80 characters.");
    }

    /// <summary>True when <c>[start, end)</c> shares at least one second with this blackout.</summary>
    public bool Overlaps(long start, long end) => start < EndSeconds && StartSeconds < end;
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
/// <para>
/// A same-family entry is therefore not a cheap change-over but a contradiction,
/// and it is rejected rather than accepted and ignored. Silently discarding
/// declared configuration is the worst of the three options: the caller has said
/// something about the shop that the engine will not honour and will not report.
/// </para>
/// </summary>
public sealed record SetupDuration(string FromFamily, string ToFamily, long DurationSeconds)
{
    /// <summary>Throws unless both families are named, distinct, and the duration is in range.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FromFamily) || string.IsNullOrWhiteSpace(ToFamily))
            throw new ArgumentOutOfRangeException(nameof(FromFamily), "Setup transition needs a source and target family.");
        if (string.Equals(FromFamily, ToFamily, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(
                nameof(ToFamily),
                FromFamily,
                "A change-over from a family to itself is never charged; declare it between two distinct families.");
        if (DurationSeconds < 0 || DurationSeconds > SchedulingParameterLimits.MaxStepDurationSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(DurationSeconds),
                DurationSeconds,
                $"Setup transition must lie in [0, {SchedulingParameterLimits.MaxStepDurationSeconds}].");
    }
}
