using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Scheduling;

/// <summary>
/// Runs the search on the UI thread, handing that thread back to the browser
/// between multi-start descents.
/// <para>
/// <b>This is not a worker.</b> The work happens on the same thread that paints
/// the page; what changes is that it happens in slices, so the browser gets to
/// repaint, deliver a click and observe a cancellation between them. A single
/// descent is atomic, so the longest the page can be unresponsive is one descent —
/// which is the run divided by <see cref="SchedulingParameters.MultiStartRuns"/>,
/// not zero. The page says so in its own wording rather than implying a background
/// thread that does not exist.
/// </para>
/// <para>
/// A real thread was measured first and does not work here: the app links SQLite
/// into the WebAssembly module, and that prebuilt object refuses the shared memory
/// <c>WasmEnableThreads</c> requires, so the app does not even link with threading
/// on. ADR 0019 has the link error and the rest of the measurements.
/// </para>
/// </summary>
/// <param name="yield">
/// How the thread is handed back. Defaults to a timer, which is right everywhere
/// except the browser this actually ships in — see <see cref="IScheduleYield"/>.
/// </param>
public sealed class CooperativeScheduleRunner(IScheduleYield? yield = null) : IScheduleRunner
{
    private readonly IScheduleYield _yield = yield ?? new TimerScheduleYield();
    private readonly SchedulingEngine _engine = new();

    /// <inheritdoc />
    public async Task<SchedulingResult> RunAsync(
        SchedulingContext context,
        IProgress<ScheduleRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var run = _engine.Begin(context);

        // Reported before anything has run, so the indicator appears with the busy
        // state rather than after the first descent - which on a large instance is
        // seconds later, and looks exactly like a frozen tab.
        progress?.Report(ScheduleRunProgress.From(run.Progress));

        while (run.RunNextRestart(cancellationToken))
        {
            progress?.Report(ScheduleRunProgress.From(run.Progress));

            // Deliberately not ConfigureAwait(false). The caller is a component:
            // what it does with each progress report is StateHasChanged, which has
            // to happen on the renderer's own context. On WebAssembly there is only
            // one thread and the distinction is invisible; it is real everywhere
            // else this code is exercised, including its tests.
            await _yield.NextTurnAsync(cancellationToken);
        }

        return run.Complete(cancellationToken);
    }
}
