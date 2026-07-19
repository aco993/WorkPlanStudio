using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Scheduling;

/// <summary>
/// Atomically completes a fenced schedule run and transitions its released orders to scheduled.
/// </summary>
public sealed class ScheduleRunFinalizer(
    ProductionDbContext db,
    ScheduleRunLeaseManager leases)
{
    public async Task<bool> TryCompleteAsync(
        Guid id,
        string workerId,
        string ownerId,
        IReadOnlyCollection<int> productionOrderIds,
        string resultJson,
        DateTime completedUtc,
        CancellationToken cancellationToken)
    {
        var orderIds = productionOrderIds.Distinct().ToArray();
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A transient exception can be raised after a successful commit. Treat an
            // already-completed run as success so execution-strategy retries are idempotent.
            if (await db.ScheduleRuns.AsNoTracking().AnyAsync(
                    run => run.Id == id && run.Status == ScheduleRunStatus.Completed,
                    cancellationToken))
                return true;

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var completed = await leases.TryCompleteAsync(
                id,
                workerId,
                resultJson,
                completedUtc,
                cancellationToken);
            if (completed != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }

            await db.ProductionOrders
                .Where(order => order.OwnerId == ownerId && orderIds.Contains(order.Id) &&
                                order.Status == ProductionOrderStatus.Released)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(order => order.Status, ProductionOrderStatus.Scheduled)
                    .SetProperty(order => order.ModifiedUtc, completedUtc)
                    .SetProperty(order => order.Version, order => order.Version + 1), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }
}
