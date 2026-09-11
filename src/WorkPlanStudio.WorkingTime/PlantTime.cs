namespace WorkPlanStudio.WorkingTime;

/// <summary>
/// The one time model this library uses: <b>plant-local wall clock</b>, carried
/// as a <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>.
/// <para>
/// A shift plan is written on the clock that hangs in the plant. "The early shift
/// starts at 06:00" is true on both sides of a daylight-saving change, so the
/// model deliberately has no time zone: attaching one to every value would force
/// a zone on callers who only ever compare two readings of the same clock.
/// </para>
/// <para>
/// The cost of that choice is that a wall-clock difference is <i>not</i> elapsed
/// time twice a year. Where the difference has to be real — the §3 and §6 (2)
/// caps measure hours actually worked, not clock positions — a
/// <see cref="TimeZoneInfo"/> is passed in explicitly and
/// <see cref="RealElapsed"/> does the conversion. Everything else stays on the
/// clock.
/// </para>
/// </summary>
public static class PlantTime
{
    /// <summary>
    /// Europe/Berlin, resolved by IANA id and falling back to the Windows id on
    /// runtimes without the ICU mapping. The library never reaches for this
    /// itself; it is here so a caller does not have to spell the fallback out.
    /// </summary>
    public static TimeZoneInfo Germany { get; } = ResolveGermany();

    /// <summary>
    /// Reads <paramref name="value"/> as a plant-local wall-clock reading and
    /// strips the kind.
    /// <para>
    /// A <see cref="DateTimeKind.Utc"/> or <see cref="DateTimeKind.Local"/> stamp
    /// is a claim about a time zone this model does not have. Converting it would
    /// need a zone; honouring it would let one record carry two different worlds
    /// (the measured defect: a segment whose <c>Start</c> is UTC and whose
    /// <c>End</c> is unspecified reports a duration that its own JSON
    /// contradicts). So the reading is kept, the claim is dropped, and every
    /// value this library hands back is unspecified.
    /// </para>
    /// </summary>
    public static DateTime Wall(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? value : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>
    /// The strict form of <see cref="Wall(DateTime)"/>: refuses a value that
    /// claims a time zone instead of quietly reinterpreting it. Use it at a
    /// boundary you control end to end.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not wall-clock.</exception>
    public static DateTime RequireWall(DateTime value, string paramName) =>
        value.Kind == DateTimeKind.Unspecified
            ? value
            : throw new ArgumentException(
                $"'{paramName}' must be a plant-local wall-clock reading (DateTimeKind.Unspecified), not {value.Kind}.", paramName);

    /// <summary>
    /// The instant a wall-clock reading names in <paramref name="zone"/>.
    /// <para>
    /// Two readings a year have no single answer. In the spring-forward gap the
    /// reading names no instant at all, and it is mapped forward by the length of
    /// the gap — the crew that was told "start at 02:30" starts when the clock
    /// says 03:30. In the autumn the reading names two instants;
    /// <paramref name="preferEarlier"/> picks the summer-time one, which is what
    /// a shift <i>start</i> means, while an <i>end</i> takes the later one so the
    /// repeated hour is counted as worked.
    /// </para>
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone, bool preferEarlier)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var value = Wall(wallClock);

        if (zone.IsAmbiguousTime(value))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(value);
            // The larger offset is summer time, and summer time comes first.
            var chosen = preferEarlier ? Max(offsets) : Min(offsets);
            return new DateTimeOffset(value, chosen);
        }

        // For an invalid reading GetUtcOffset reports the offset in force before
        // the jump, so subtracting it lands past the gap — exactly the forward
        // mapping described above.
        return new DateTimeOffset(value, zone.GetUtcOffset(value));
    }

    /// <summary>
    /// Hours actually elapsed between two wall-clock readings in
    /// <paramref name="zone"/> — 7 h for a 22:00–06:00 night across the
    /// spring-forward, 9 h across the autumn one. This is the figure §3 and
    /// §6 (2) measure; the clock difference is not.
    /// </summary>
    public static TimeSpan RealElapsed(DateTime fromWallClock, DateTime toWallClock, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return ToInstant(toWallClock, zone, preferEarlier: false) - ToInstant(fromWallClock, zone, preferEarlier: true);
    }

    private static TimeSpan Max(TimeSpan[] values)
    {
        var best = values[0];
        foreach (var value in values)
            if (value > best)
                best = value;
        return best;
    }

    private static TimeSpan Min(TimeSpan[] values)
    {
        var best = values[0];
        foreach (var value in values)
            if (value < best)
                best = value;
        return best;
    }

    private static TimeZoneInfo ResolveGermany()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the other spelling.
            }
            catch (InvalidTimeZoneException)
            {
                // Corrupt entry in the registry or the tz database.
            }
        }

        return TimeZoneInfo.Utc;
    }
}
