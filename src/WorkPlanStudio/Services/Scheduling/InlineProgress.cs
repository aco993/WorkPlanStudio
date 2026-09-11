namespace WorkPlanStudio.Services.Scheduling;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback on the caller's thread,
/// immediately.
/// <para>
/// <see cref="Progress{T}"/> posts to the captured synchronization context
/// instead. On a host with one thread that is not an improvement but a hazard:
/// the posted callbacks queue up behind the run that produced them, so the last
/// few arrive <i>after</i> the result has been rendered and redraw a progress bar
/// over a finished schedule. Reporting inline keeps the indicator in step with
/// the search, which is the only reason it exists.
/// </para>
/// </summary>
/// <typeparam name="T">The progress value.</typeparam>
/// <param name="report">Called with each reported value.</param>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Action<T> _report = report ?? throw new ArgumentNullException(nameof(report));

    /// <inheritdoc />
    public void Report(T value) => _report(value);
}
