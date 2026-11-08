namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The tests that measure the machine rather than the answer, kept off the
/// machine while anything else is on it.
/// </summary>
/// <remarks>
/// <para>
/// xunit runs test classes in parallel, so a stopwatch in one of them times the
/// engine <em>plus</em> whatever the other two hundred tests are doing to the
/// cores at that moment. Measured here: the medium problem takes about 5 s on
/// an idle machine against a 4 s budget it comfortably meets, and 11.5 s in a
/// full-suite run. That is not the engine being slow, and a tripwire that
/// reddens for a reason unrelated to the code it guards teaches the author to
/// re-run rather than to look.
/// </para>
/// <para>
/// <c>DisableParallelization</c> keeps this collection from running alongside
/// any other, which is the only arrangement in which a wall-clock number means
/// anything. <see cref="AllocationBudgetTests"/> joins it for a second reason:
/// it calls <c>GC.Collect()</c>, and a forced collection in the middle of
/// somebody else's timing is the same problem pointed the other way.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClock
{
    public const string Name = "wall clock";

    /// <summary>
    /// Whether the wall-clock tripwires run at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// They are off unless asked for, the same way the visual baselines are off
    /// away from Linux and the documentation capture is off without its own
    /// variable. CI already worked this way — <c>ci.yml</c> filters them out of
    /// the pull-request gate and <c>performance.yml</c> runs them on their own —
    /// and the only place that was not true was a developer's own full run,
    /// where they reddened for the machine rather than for the code.
    /// </para>
    /// <para>
    /// Measured on one laptop: the medium problem alone takes about 4.6 s for
    /// the whole class and passes; the same class at the end of a 294-test run
    /// took 4,763 ms against its 4,000 ms bound. Serialising the collection took
    /// that from 11,513 ms, so most of it was contention — what is left is a hot
    /// CPU and a large heap, which no amount of scheduling fixes and which is
    /// exactly why these belong in a run of their own.
    /// </para>
    /// </remarks>
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("RUN_WALL_CLOCK_BUDGETS") == "1";

    internal const string SkipReason =
        "wall-clock tripwires run on their own, not inside the suite: set RUN_WALL_CLOCK_BUDGETS=1 "
        + "(performance.yml does) so the number measures the code and not the machine it shares.";

}
