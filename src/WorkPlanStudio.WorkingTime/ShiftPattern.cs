namespace WorkPlanStudio.WorkingTime;

/// <summary>The days of the week a shift is staffed, as flags.</summary>
[Flags]
public enum WorkDays
{
    /// <summary>No day.</summary>
    None = 0,
    /// <summary>Monday.</summary>
    Monday = 1,
    /// <summary>Tuesday.</summary>
    Tuesday = 2,
    /// <summary>Wednesday.</summary>
    Wednesday = 4,
    /// <summary>Thursday.</summary>
    Thursday = 8,
    /// <summary>Friday.</summary>
    Friday = 16,
    /// <summary>Saturday.</summary>
    Saturday = 32,
    /// <summary>Sunday.</summary>
    Sunday = 64,
    /// <summary>Monday to Friday.</summary>
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    /// <summary>Every day.</summary>
    All = Weekdays | Saturday | Sunday
}

/// <summary>Helpers for <see cref="WorkDays"/>.</summary>
public static class WorkDaysExtensions
{
    /// <summary>The flag for a <see cref="DayOfWeek"/>.</summary>
    public static WorkDays ToFlag(this DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WorkDays.Monday,
        DayOfWeek.Tuesday => WorkDays.Tuesday,
        DayOfWeek.Wednesday => WorkDays.Wednesday,
        DayOfWeek.Thursday => WorkDays.Thursday,
        DayOfWeek.Friday => WorkDays.Friday,
        DayOfWeek.Saturday => WorkDays.Saturday,
        _ => WorkDays.Sunday
    };

    /// <summary>True when <paramref name="day"/> is staffed.</summary>
    public static bool Includes(this WorkDays days, DayOfWeek day) => (days & day.ToFlag()) != 0;
}

/// <summary>
/// One shift: a crew that starts and ends at fixed times of day on the given
/// days. An end before the start means the shift runs past midnight.
/// </summary>
/// <param name="Key">Stable identifier and default crew key, e.g. <c>early</c>.</param>
/// <param name="Start">Clock-in time.</param>
/// <param name="End">Clock-out time; earlier than <paramref name="Start"/> for a night shift.</param>
/// <param name="Days">The days on which the shift <i>starts</i>.</param>
/// <param name="CrewKey">
/// Which crew works the shift. The §5 rest period is enforced between
/// consecutive shifts of the same crew, so two shift definitions that share a
/// crew (a Friday late shift and a Saturday early shift, say) are checked
/// against each other. Defaults to <paramref name="Key"/>.
/// </param>
public sealed record ShiftDefinition(
    string Key,
    TimeOnly Start,
    TimeOnly End,
    WorkDays Days,
    string? CrewKey = null)
{
    /// <summary>The crew that works this shift.</summary>
    public string Crew => CrewKey ?? Key;

    /// <summary>Length of the shift, midnight crossings included.</summary>
    public TimeSpan Duration
    {
        get
        {
            var span = End.ToTimeSpan() - Start.ToTimeSpan();
            return span <= TimeSpan.Zero ? span + TimeSpan.FromDays(1) : span;
        }
    }

    /// <summary>Throws unless the definition is usable.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 40)
            throw new ArgumentException("A shift needs a key of at most 40 characters.");
        if (Days == WorkDays.None)
            throw new ArgumentException($"Shift '{Key}' is staffed on no day.");
        if (Start == End)
            throw new ArgumentException($"Shift '{Key}' starts and ends at the same time.");
    }
}

/// <summary>A named set of shifts that together describe how a work center is staffed.</summary>
/// <param name="Key">Stable identifier, e.g. <c>two-shift</c>.</param>
/// <param name="Shifts">The shifts, in any order.</param>
public sealed record ShiftPattern(string Key, IReadOnlyList<ShiftDefinition> Shifts)
{
    /// <summary>True when there is no shift at all — the work center is available around the clock.</summary>
    public bool IsContinuous => Shifts.Count == 0;

    /// <summary>Throws unless every shift is usable and keys are unique.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 40)
            throw new ArgumentException("A shift pattern needs a key of at most 40 characters.");
        foreach (var shift in Shifts)
            shift.Validate();
        if (Shifts.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != Shifts.Count)
            throw new ArgumentException($"Shift pattern '{Key}' has duplicate shift keys.");
    }
}

/// <summary>The shift models most plants run, ready to use.</summary>
public static class ShiftPatterns
{
    /// <summary>Available 24/7 — no working-time constraint at all (an unattended machine).</summary>
    public static ShiftPattern Continuous { get; } = new("continuous", []);

    /// <summary>One day shift 07:00–15:30, Monday to Friday. 8 h working plus the §4 break.</summary>
    public static ShiftPattern OneShift { get; } = new("one-shift",
    [
        new ShiftDefinition("day", new TimeOnly(7, 0), new TimeOnly(15, 30), WorkDays.Weekdays)
    ]);

    /// <summary>Early 06:00–14:00 and late 14:00–22:00, Monday to Friday.</summary>
    public static ShiftPattern TwoShift { get; } = new("two-shift",
    [
        new ShiftDefinition("early", new TimeOnly(6, 0), new TimeOnly(14, 0), WorkDays.Weekdays),
        new ShiftDefinition("late", new TimeOnly(14, 0), new TimeOnly(22, 0), WorkDays.Weekdays)
    ]);

    /// <summary>Early, late and night (22:00–06:00), Monday to Friday; the Friday night shift runs into Saturday.</summary>
    public static ShiftPattern ThreeShift { get; } = new("three-shift",
    [
        new ShiftDefinition("early", new TimeOnly(6, 0), new TimeOnly(14, 0), WorkDays.Weekdays),
        new ShiftDefinition("late", new TimeOnly(14, 0), new TimeOnly(22, 0), WorkDays.Weekdays),
        new ShiftDefinition("night", new TimeOnly(22, 0), new TimeOnly(6, 0), WorkDays.Weekdays)
    ]);

    /// <summary>All presets, in the order a UI should list them.</summary>
    public static IReadOnlyList<ShiftPattern> Presets { get; } = [Continuous, OneShift, TwoShift, ThreeShift];

    /// <summary>The preset with the given key, or null.</summary>
    public static ShiftPattern? ByKey(string? key) =>
        Presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
}
