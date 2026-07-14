using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Scheduling;

/// <summary>Provider-neutral atomic database lease operations for horizontally scaled workers.</summary>
public sealed class ScheduleRunLeaseManager(ProductionDbContext db)
{
    public async Task<bool> TryClaimAsync(
        Guid id,
        string workerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var affected = await db.ScheduleRuns
            .Where(run => run.Id == id && run.CancellationRequestedUtc == null &&
                          (run.Status == ScheduleRunStatus.Queued ||
                           (run.Status == ScheduleRunStatus.Running &&
                            (run.LeaseExpiresUtc == null || run.LeaseExpiresUtc < nowUtc))))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.Status, ScheduleRunStatus.Running)
                .SetProperty(run => run.ProgressPercent, 5)
                .SetProperty(run => run.StartedUtc, nowUtc)
                .SetProperty(run => run.LeaseOwner, workerId)
                .SetProperty(run => run.LeaseExpiresUtc, nowUtc.Add(leaseDuration))
                .SetProperty(run => run.AttemptCount, run => run.AttemptCount + 1), cancellationToken);
        return affected == 1;
    }

    public Task<int> RenewAsync(
        Guid id,
        string workerId,
        DateTime expiresUtc,
        CancellationToken cancellationToken) =>
        db.ScheduleRuns.Where(run => run.Id == id && run.LeaseOwner == workerId &&
                                     run.Status == ScheduleRunStatus.Running && run.CancellationRequestedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.LeaseExpiresUtc, expiresUtc), cancellationToken);

    public Task<int> TryCompleteAsync(
        Guid id,
        string workerId,
        string resultJson,
        DateTime completedUtc,
        CancellationToken cancellationToken) =>
        db.ScheduleRuns.Where(run => run.Id == id && run.LeaseOwner == workerId &&
                                     run.Status == ScheduleRunStatus.Running && run.CancellationRequestedUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.Status, ScheduleRunStatus.Completed)
                .SetProperty(run => run.ProgressPercent, 100)
                .SetProperty(run => run.ResultJson, resultJson)
                .SetProperty(run => run.CompletedUtc, completedUtc)
                .SetProperty(run => run.LeaseOwner, (string?)null)
                .SetProperty(run => run.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(run => run.Version, run => run.Version + 1), cancellationToken);
}
