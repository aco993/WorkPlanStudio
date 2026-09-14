using Microsoft.JSInterop;

namespace WorkPlanStudio.Services.Scheduling;

/// <summary>
/// Yields through a <c>MessageChannel</c> task rather than a timer, so the browser
/// gets its turn without the clamping and throttling a timer is subject to.
/// <para>
/// Measured on this app, in Chrome, with the tab hidden: a 64-restart run yielding
/// through <c>Task.Delay(1)</c> advanced from 25 % to 27 % in ten seconds, because
/// a background tab's timers are throttled to about one wake-up a second. The same
/// run yielding through a message port finishes at full speed whether the tab is
/// visible or not. A hidden tab is not an exotic case — it is what a tab is while
/// the planner reads the order list in another one.
/// </para>
/// <para>
/// If the interop call fails for any reason the fallback is the timer, because a
/// run that is slow is better than a run that does not happen.
/// </para>
/// </summary>
public sealed class BrowserScheduleYield : IScheduleYield
{
    private readonly IJSRuntime _js;
    private readonly TimerScheduleYield _fallback = new();
    private bool _interopBroken;

    public BrowserScheduleYield(IJSRuntime js) => _js = js;

    /// <inheritdoc />
    public async Task NextTurnAsync(CancellationToken cancellationToken)
    {
        if (_interopBroken)
        {
            await _fallback.NextTurnAsync(cancellationToken);
            return;
        }

        try
        {
            await _js.InvokeVoidAsync("workplanYield.next", cancellationToken);
        }
        catch (JSException)
        {
            // Prerendering, a stripped app.js, or a host with no DOM. Said once,
            // by the flag, rather than paid for on every restart of every run.
            _interopBroken = true;
            await _fallback.NextTurnAsync(cancellationToken);
        }
    }
}
