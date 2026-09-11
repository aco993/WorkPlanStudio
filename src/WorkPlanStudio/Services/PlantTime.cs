namespace WorkPlanStudio.Services;

/// <summary>
/// The application's one time model, in one place, because it previously had
/// three and converted between none of them.
/// <para>
/// <b>Every planning date is plant-local wall-clock time.</b> An order's release
/// and due dates, a work centre's absences, the shift boundaries, the §9(2)
/// Sunday window and the holiday closures are all read as the clock on the wall
/// of the plant. Nothing in the application calls <c>ToLocalTime</c> or
/// <c>ToUniversalTime</c>, so a visitor's own time zone cannot move a shift.
/// </para>
/// <para>
/// The exception is deliberate and narrow: <c>CreatedUtc</c> and
/// <c>ModifiedUtc</c> are audit stamps and are genuinely UTC. They are never
/// mixed into the calendar.
/// </para>
/// <para>
/// The cost of this decision is stated plainly rather than hidden: the plant's
/// zone is not modelled, so the app cannot yet say "18:00 in Berlin" to a
/// visitor in another zone. It does not pretend to. What it does guarantee is
/// that the stored value, the value on screen and the value the scheduler reasons
/// about are the same number. EF's SQLite provider writes a <c>DateTime</c>
/// without an offset and reads it back as <see cref="DateTimeKind.Unspecified"/>,
/// which is exactly what a wall-clock value should be — so the boundary
/// normalises rather than converts.
/// </para>
/// </summary>
public static class PlantTime
{
    /// <summary>
    /// Strips a <see cref="DateTimeKind"/> that a UI control or a test may have
    /// attached, without moving the value. A wall-clock instant has no offset;
    /// keeping <c>Utc</c> on it is what let the old <c>ReleaseUtc</c> name
    /// survive as long as it did.
    /// </summary>
    public static DateTime WallClock(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? value : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>The plant-local date part, for a control that binds a date without a time.</summary>
    public static DateTime Date(DateTime value) => WallClock(value).Date;
}
