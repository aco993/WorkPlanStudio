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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseHeartbeat = TimeSpan.FromSeconds(10);
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Schedule worker {WorkerId} started with durable database leases", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            await QueueEligibleRunsAsync(stoppingToken);
            while (queue.TryRead(out var id))
                await ProcessAsync(id, stoppingToken);
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task QueueEligibleRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        var now = DateTime.UtcNow;
        var ids = await db.ScheduleRuns.AsNoTracking()
            .Where(run => run.CancellationRequestedUtc == null &&
                          (run.Status == ScheduleRunStatus.Queued ||
                           (run.Status == ScheduleRunStatus.Running &&
                            (run.LeaseExpiresUtc == null || run.LeaseExpiresUtc < now))))
            .OrderBy(run => run.CreatedUtc)
            .Select(run => run.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
            _ = queue.TryQueue(id);
    }

    private async Task ProcessAsync(Guid id, CancellationToken stoppingToken)
    {
        if (!await TryClaimAsync(id, stoppingToken))
            return;

        var cancellationToken = queue.Register(id, stoppingToken);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorLeaseAsync(id, monitorStop.Token);
        try
        {
            await RunAsync(id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishAsync(id, ScheduleRunStatus.Cancelled, "cancelled", null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Schedule run {ScheduleRunId} failed on worker {WorkerId}", id, _workerId);
            await FinishAsync(id, ScheduleRunStatus.Failed, "scheduling_failed", null, CancellationToken.None);
        }
        finally
        {
            monitorStop.Cancel();
            try
            {
                await monitor;
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested)
            {
            }
            queue.Complete(id);
        }
    }

    private async Task<bool> TryClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var leases = scope.ServiceProvider.GetRequiredService<ScheduleRunLeaseManager>();
        var now = DateTime.UtcNow;
        return await leases.TryClaimAsync(id, _workerId, now, LeaseDuration, cancellationToken);
    }

    private async Task MonitorLeaseAsync(Guid id, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(LeaseHeartbeat);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
            var state = await db.ScheduleRuns.AsNoTracking()
                .Where(run => run.Id == id)
                .Select(run => new { run.LeaseOwner, run.CancellationRequestedUtc })
                .FirstOrDefaultAsync(cancellationToken);
            if (state is null || state.LeaseOwner != _workerId || state.CancellationRequestedUtc is not null)
            {
                queue.Cancel(id);
                return;
            }
            var leases = scope.ServiceProvider.GetRequiredService<ScheduleRunLeaseManager>();
            if (await leases.RenewAsync(id, _workerId, DateTime.UtcNow.Add(LeaseDuration), cancellationToken) != 1)
            {
                queue.Cancel(id);
                return;
            }
        }
    }

    private async Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        var run = await db.ScheduleRuns.FirstOrDefaultAsync(
            item => item.Id == id && item.LeaseOwner == _workerId &&
                    item.Status == ScheduleRunStatus.Running && item.CancellationRequestedUtc == null,
            cancellationToken);
        if (run is null)
        {
            queue.Cancel(id);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

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
                    index + 1, operation.WorkCenterId, ProcessingSeconds(operation, order.Quantity), operation.SetupFamily))
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

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var leases = scope.ServiceProvider.GetRequiredService<ScheduleRunLeaseManager>();
        var completed = await leases.TryCompleteAsync(
            id,
            _workerId,
            JsonSerializer.Serialize(new { HorizonStartUtc = horizonStart, Result = result }),
            DateTime.UtcNow,
            cancellationToken);
        if (completed != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            queue.Cancel(id);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The schedule run lease was lost before completion.");
        }
        var trackedOrders = await db.ProductionOrders.Where(order => order.OwnerId == run.OwnerId && request.ProductionOrderIds.Contains(order.Id))
            .ToListAsync(cancellationToken);
        foreach (var order in trackedOrders.Where(order => order.Status == ProductionOrderStatus.Released))
        {
            order.Status = ProductionOrderStatus.Scheduled;
            order.ModifiedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FinishAsync(
        Guid id,
        ScheduleRunStatus status,
        string error,
        string? result,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        await db.ScheduleRuns.Where(run => run.Id == id && run.LeaseOwner == _workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.Status, status)
                .SetProperty(run => run.ErrorCode, error)
                .SetProperty(run => run.ResultJson, result)
                .SetProperty(run => run.CompletedUtc, DateTime.UtcNow)
                .SetProperty(run => run.LeaseOwner, (string?)null)
                .SetProperty(run => run.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(run => run.Version, run => run.Version + 1), cancellationToken);
    }

    private static long ProcessingSeconds(OperationDto operation, int quantity) => checked((long)decimal.Round(
        checked((operation.SetupTimeMinutes + checked(operation.TimePerPieceMinutes * quantity)) * 60m),
        MidpointRounding.ToEven));

    private static long Seconds(DateTime horizonStart, DateTime value) =>
        checked((long)(value.ToUniversalTime() - horizonStart).TotalSeconds);
}
