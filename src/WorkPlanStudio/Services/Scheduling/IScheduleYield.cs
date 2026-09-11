namespace WorkPlanStudio.Services.Scheduling;

/// <summary>
/// Hands the current thread back to whatever is scheduling it, and resumes on the
/// next turn.
/// <para>
/// It is an abstraction because the primitive matters and is host-specific. In a
/// browser tab a timer is the obvious choice and the wrong one: a nested
/// <c>setTimeout</c> is clamped to about four milliseconds, and a tab in the
/// background is throttled to roughly one wake-up a second — measured here as a
/// run that advanced two restarts in ten seconds once the tab was hidden. A task
/// posted through a message port is not a timer and is not throttled that way.
/// </para>
/// </summary>
public interface IScheduleYield
{
    /// <summary>Completes on a later turn of the host's loop.</summary>
    Task NextTurnAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Yields with a one-millisecond delay: portable, dependency-free, and the right
/// default outside a browser — every host this runs in other than WebAssembly has
/// a real scheduler behind <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// One millisecond rather than zero: <c>Task.Delay(0)</c> returns an already
/// completed task and yields nothing at all.
/// </remarks>
public sealed class TimerScheduleYield : IScheduleYield
{
    /// <inheritdoc />
    public Task NextTurnAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
}
