using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WorkPlanStudio.Api.Data;

namespace WorkPlanStudio.Api.Health;

/// <summary>
/// Readiness: can this instance actually answer a request?
/// <para>
/// Liveness (<c>/health</c>) deliberately asks nothing — a process that is up but
/// whose database is unreachable should be left alone to recover, not killed and
/// restarted into the same outage. Readiness (<c>/health/ready</c>) is the one
/// that takes the instance out of rotation, so it is the one that touches the
/// store.
/// </para>
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly ApiDbContext _db;

    /// <summary>Creates the check.</summary>
    /// <param name="db">The store to probe.</param>
    public DatabaseHealthCheck(ApiDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A connect, not a query: the point is reachability, and a probe that
            // scans a table turns a health endpoint into a load source.
            return await _db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("The database is reachable.")
                : HealthCheckResult.Unhealthy("The database is not reachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message is deliberately not the exception's: a health endpoint
            // is usually reachable from further away than the rest of the API.
            return HealthCheckResult.Unhealthy("The database is not reachable.");
        }
    }
}
