using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WorkPlanStudio.Api.Telemetry;

/// <summary>
/// The API's own spans and instruments, named once.
/// <para>
/// ASP.NET Core already reports every request as a span and as
/// <c>http.server.request.duration</c>. What it cannot see is what a request
/// <em>did</em>: how many orders a schedule run read, how long the search took
/// as opposed to the database reads around it, whether it ended in a schedule
/// or in a preparation error, and how often sign-in is being refused. Those are
/// the questions an operator asks first, so they get names here.
/// </para>
/// <para>
/// Nothing that identifies a person goes into a tag: no user name, no user id,
/// no client address. A rejected sign-in is counted by reason, which is enough
/// to see a guessing campaign and not enough to build a list of accounts.
/// </para>
/// </summary>
public sealed class ApiTelemetry : IDisposable
{
    /// <summary>The name both the <see cref="ActivitySource"/> and the <see cref="Meter"/> are registered under.</summary>
    public const string Name = "WorkPlanStudio.Api";

    /// <summary>Spans for work inside a request. Static because a span source has no per-host state.</summary>
    public static readonly ActivitySource Source = new(Name);

    private readonly Meter _meter;

    /// <summary>Creates the instruments on a meter owned by the host's <see cref="IMeterFactory"/>.</summary>
    /// <param name="meters">The host's meter factory; tests resolve the same one and listen to it.</param>
    public ApiTelemetry(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _meter = meters.Create(Name);

        ScheduleRunDuration = _meter.CreateHistogram<double>(
            "workplan.schedule.run.duration", unit: "s",
            description: "Wall-clock time of a server-side schedule run, reads included, by outcome.");
        ScheduledOperations = _meter.CreateCounter<long>(
            "workplan.schedule.operations", unit: "{operation}",
            description: "Operations placed by server-side schedule runs.");
        SignInRejected = _meter.CreateCounter<long>(
            "workplan.auth.sign_in.rejected", unit: "{attempt}",
            description: "Sign-in attempts refused, by reason. No account is identified.");
    }

    /// <summary>Seconds per schedule run, tagged <c>outcome</c>: <c>scheduled</c>, <c>not_schedulable</c>, <c>invalid_parameters</c> or <c>cancelled</c>.</summary>
    public Histogram<double> ScheduleRunDuration { get; }

    /// <summary>Operations placed, so throughput can be read as operations per second rather than runs.</summary>
    public Counter<long> ScheduledOperations { get; }

    /// <summary>Refused sign-ins, tagged <c>reason</c>: <c>invalid_credentials</c> or <c>locked_out</c>.</summary>
    public Counter<long> SignInRejected { get; }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
