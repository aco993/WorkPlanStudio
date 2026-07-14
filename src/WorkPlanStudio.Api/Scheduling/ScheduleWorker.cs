using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Api.Scheduling;

public sealed class ScheduleWorker(
    ScheduleRunQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedRunsAsync(stoppingToken);
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            var cancellationToken = queue.Register(id, stoppingToken);
            try
            {
                await RunAsync(id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await FinishAsync(id, ScheduleRunStatus.Cancelled, "cancelled", null, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Schedule run {ScheduleRunId} failed", id);
                await FinishAsync(id, ScheduleRunStatus.Failed, "scheduling_failed", null, stoppingToken);
            }
            finally
            {
                queue.Complete(id);
            }
        }
    }

    private async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        var pending = await db.ScheduleRuns
            .Where(run => run.Status == ScheduleRunStatus.Queued || run.Status == ScheduleRunStatus.Running)
            .OrderBy(run => run.CreatedUtc)
            .ToListAsync(cancellationToken);
        foreach (var run in pending)
        {
            run.Status = ScheduleRunStatus.Queued;
            run.ProgressPercent = 0;
            run.StartedUtc = null;
            if (!queue.TryQueue(run.Id))
            {
                run.Status = ScheduleRunStatus.Failed;
                run.ErrorCode = "queue_capacity_exceeded";
                run.CompletedUtc = DateTime.UtcNow;
            }
        }
        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Recovered {Count} persisted schedule runs after startup", pending.Count);
        }
    }

    private async Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        var run = await db.ScheduleRuns.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (run is null || run.Status == ScheduleRunStatus.Cancelled)
            return;
        run.Status = ScheduleRunStatus.Running;
        run.ProgressPercent = 5;
        run.StartedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var request = JsonSerializer.Deserialize<CreateScheduleRunRequest>(run.ParametersJson)
            ?? throw new InvalidOperationException("Schedule parameters are invalid.");
        var orders = await db.ProductionOrders.AsNoTracking()
            .Where(order => order.OwnerId == run.OwnerId && request.ProductionOrderIds.Contains(order.Id) &&
                            (order.Status == ProductionOrderStatus.Released || order.Status == ProductionOrderStatus.Scheduled))
            .ToListAsync(cancellationToken);
        if (orders.Count != request.ProductionOrderIds.Distinct().Count())
            throw new InvalidOperationException("One or more production orders are missing or not released.");

        var horizonStart = orders.Min(order => order.ReleaseUtc).ToUniversalTime();
        var horizonEnd = orders.Max(order => order.DueUtc).ToUniversalTime().AddDays(30);
        var snapshots = orders.ToDictionary(
            order => order.Id,
            order => JsonSerializer.Deserialize<WorkPlanDto>(order.RoutingSnapshotJson)
                     ?? throw new InvalidOperationException($"Order {order.Id} has an invalid routing snapshot."));
        var centerIds = snapshots.Values.SelectMany(plan => plan.Operations).Select(operation => operation.WorkCenterId).Distinct().ToList();
        var centers = await db.WorkCenters.AsNoTracking()
            .Include(center => center.CalendarShifts).Include(center => center.Downtimes).Include(center => center.SetupTransitions)
            .Where(center => center.OwnerId == run.OwnerId && centerIds.Contains(center.Id) && center.IsActive)
            .ToListAsync(cancellationToken);
        if (centers.Count != centerIds.Count)
            throw new InvalidOperationException("A routing references a missing or inactive work center.");

        run.ProgressPercent = 25;
        await db.SaveChangesAsync(cancellationToken);
        var machines = centers.Select(center => CapacityWindowBuilder.Build(center, horizonStart, horizonEnd)).ToList();
        var jobs = orders.Select(order => new ProductionJob
        {
            Id = order.Id,
            Reference = order.OrderNumber,
            ReleaseSeconds = Seconds(horizonStart, order.ReleaseUtc),
            ExplicitDueSeconds = Seconds(horizonStart, order.DueUtc),
            Weight = order.Priority,
            Steps = snapshots[order.Id].Operations.OrderBy(operation => operation.OperationNumber)
                .Select((operation, index) => new JobStep(
                    index + 1,
                    operation.WorkCenterId,
                    ProcessingSeconds(operation, order.Quantity),
                    operation.SetupFamily))
                .ToList()
        }).ToList();
        var parameters = new SchedulingParameters
        {
            DueDateRule = DueDateRule.Explicit,
            DispatchRule = DispatchRule.EarliestDueDate,
            MultiStartRuns = request.MultiStartRuns,
            LocalSearchMaxSteps = request.LocalSearchMaxSteps,
            Seed = request.Seed
        };
        var result = new SchedulingEngine().RunCancellable(new SchedulingContext(jobs, machines, parameters), cancellationToken);

        run.Status = ScheduleRunStatus.Completed;
        run.ProgressPercent = 100;
        run.ResultJson = JsonSerializer.Serialize(new { HorizonStartUtc = horizonStart, Result = result });
        run.CompletedUtc = DateTime.UtcNow;
        var trackedOrders = await db.ProductionOrders.Where(order => order.OwnerId == run.OwnerId && request.ProductionOrderIds.Contains(order.Id))
            .ToListAsync(cancellationToken);
        foreach (var order in trackedOrders.Where(order => order.Status == ProductionOrderStatus.Released))
        {
            order.Status = ProductionOrderStatus.Scheduled;
            order.ModifiedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishAsync(Guid id, ScheduleRunStatus status, string error, string? result, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        var run = await db.ScheduleRuns.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (run is null)
            return;
        run.Status = status;
        run.ErrorCode = error;
        run.ResultJson = result;
        run.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static long ProcessingSeconds(OperationDto operation, int quantity) => checked((long)decimal.Round(
        checked((operation.SetupTimeMinutes + checked(operation.TimePerPieceMinutes * quantity)) * 60m),
        MidpointRounding.ToEven));

    private static long Seconds(DateTime horizonStart, DateTime value) => checked((long)(value.ToUniversalTime() - horizonStart).TotalSeconds);
}
