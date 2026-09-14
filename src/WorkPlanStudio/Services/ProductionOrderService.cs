using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services;

/// <summary>
/// Production orders and the one operation that matters: release, which freezes
/// the routing.
/// </summary>
public sealed class ProductionOrderService
{
    private const string NumberPrefix = "PO-";

    private readonly BrowserDatabase _db;
    private readonly IPermissionGuard _guard;

    public ProductionOrderService(BrowserDatabase db, IPermissionGuard? guard = null)
    {
        _db = db;
        _guard = guard ?? AllowAllGuard.Instance;
    }

    public async Task<List<ProductionOrder>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.ProductionOrders
            .Include(o => o.WorkPlan)
            .AsNoTracking()
            .OrderByDescending(o => o.DueLocal)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Orders whose routing is frozen and which therefore can be scheduled.</summary>
    public async Task<List<ProductionOrder>> GetSchedulableAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.ProductionOrders
            .AsNoTracking()
            .Where(o => o.Status == ProductionOrderStatus.Released && o.RoutingSnapshotJson != "")
            .OrderBy(o => o.DueLocal)
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductionOrder?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.ProductionOrders
            .Include(o => o.WorkPlan)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    /// <summary>Suggests the next free order number, e.g. "PO-1004".</summary>
    public async Task<string> SuggestOrderNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        var taken = await db.ProductionOrders.Select(o => o.OrderNumber).ToListAsync(cancellationToken);
        return NumberSuggestion.Next(NumberPrefix, taken);
    }

    public async Task<ApplicationResult<ProductionOrder>> SaveAsync(
        ProductionOrder order,
        CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageOrders, cancellationToken))
            return ApplicationResult<ProductionOrder>.Forbidden();

        ArgumentNullException.ThrowIfNull(order);
        Normalize(order);

        var issues = ProductionOrderValidator.Validate(order);
        if (issues.Count > 0)
            return ApplicationResult<ProductionOrder>.Validation(issues);

        return await DatabaseMutation.RunAsync<ProductionOrder>(
            _db,
            async (db, token) =>
            {
                var plan = await db.WorkPlans.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == order.WorkPlanId, token);
                if (plan is null)
                    return ApplicationResult<Func<ProductionOrder>>.Validation(
                        [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_WorkPlanMissing")]);

                // The dropdown only offers released plans; that was the only thing
                // enforcing it, which put a manufacturing sign-off in a .razor file.
                if (plan.Status != WorkPlanStatus.Released)
                    return ApplicationResult<Func<ProductionOrder>>.Validation(
                        [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_PlanNotReleased")]);

                if (await db.ProductionOrders.AnyAsync(
                        o => o.OrderNumber == order.OrderNumber && o.Id != order.Id, token))
                    return ApplicationResult<Func<ProductionOrder>>.Conflict(
                        new ValidationIssue(nameof(ProductionOrder.OrderNumber), "Val_OrderNumberTaken"));

                ProductionOrder entity;
                if (order.Id == 0)
                {
                    entity = new ProductionOrder { CreatedUtc = DateTime.UtcNow };
                    db.ProductionOrders.Add(entity);
                }
                else
                {
                    var existing = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == order.Id, token);
                    if (existing is null)
                        return ApplicationResult<Func<ProductionOrder>>.NotFound();

                    // The snapshot is the whole point: once frozen, the terms of the
                    // order are a record of what the shop was told to build.
                    if (existing.Status != ProductionOrderStatus.Draft)
                        return ApplicationResult<Func<ProductionOrder>>.Conflict(
                            new ValidationIssue(nameof(ProductionOrder.Status), "Val_OrderNotDraft"));

                    entity = existing;
                }

                entity.OrderNumber = order.OrderNumber;
                entity.WorkPlanId = order.WorkPlanId;
                entity.Quantity = order.Quantity;
                entity.ReleaseLocal = order.ReleaseLocal;
                entity.DueLocal = order.DueLocal;
                entity.Priority = order.Priority;
                entity.ModifiedUtc = DateTime.UtcNow;

                return ApplicationResult<Func<ProductionOrder>>.Success(() => entity);
            },
            new ValidationIssue(nameof(ProductionOrder.OrderNumber), "Val_OrderNumberTaken"),
            cancellationToken);
    }

    /// <summary>
    /// Freezes the current routing onto the order and releases it.
    /// </summary>
    /// <remarks>
    /// This used to claim it was "the moment the order stops depending on master
    /// data". It never was: the snapshot stores work-centre ids and the scheduler
    /// resolves them against live master data on every run. What the release does
    /// now is record that dependency in <see cref="OrderRoutingCenter"/> rows, so
    /// the guards and the database can protect it.
    /// </remarks>
    public async Task<ApplicationResult<ProductionOrder>> ReleaseAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageOrders, cancellationToken))
            return ApplicationResult<ProductionOrder>.Forbidden();

        return await DatabaseMutation.RunAsync<ProductionOrder>(
            _db,
            async (db, token) =>
            {
                var order = await db.ProductionOrders
                    .Include(o => o.RoutingCenters)
                    .FirstOrDefaultAsync(o => o.Id == id, token);
                if (order is null)
                    return ApplicationResult<Func<ProductionOrder>>.NotFound();

                if (order.Status != ProductionOrderStatus.Draft)
                    return ApplicationResult<Func<ProductionOrder>>.Conflict(
                        new ValidationIssue(nameof(ProductionOrder.Status), "Val_OrderNotDraft"));

                var plan = await db.WorkPlans
                    .Include(p => p.Operations).ThenInclude(o => o.WorkCenter)
                    .FirstOrDefaultAsync(p => p.Id == order.WorkPlanId, token);

                if (plan is null)
                    return ApplicationResult<Func<ProductionOrder>>.Validation(
                        [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_WorkPlanMissing")]);

                if (plan.Status != WorkPlanStatus.Released)
                    return ApplicationResult<Func<ProductionOrder>>.Validation(
                        [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_PlanNotReleased")]);

                if (plan.Operations.Count == 0)
                    return ApplicationResult<Func<ProductionOrder>>.Validation(
                        [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_OrderPlanHasNoOperations")]);

                var snapshot = RoutingSnapshot.Capture(plan);
                order.RoutingSnapshotJson = snapshot.Serialize();
                order.RoutingRevision = plan.Revision ?? "";
                order.Status = ProductionOrderStatus.Released;
                order.ModifiedUtc = DateTime.UtcNow;

                order.RoutingCenters.Clear();
                foreach (var workCenterId in snapshot.WorkCenterIds)
                    order.RoutingCenters.Add(new OrderRoutingCenter { WorkCenterId = workCenterId });

                return ApplicationResult<Func<ProductionOrder>>.Success(() => order);
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Withdraws an order. The snapshot is kept as the record of what was released.</summary>
    public async Task<ApplicationResult<ProductionOrder>> CancelAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageOrders, cancellationToken))
            return ApplicationResult<ProductionOrder>.Forbidden();

        return await DatabaseMutation.RunAsync<ProductionOrder>(
            _db,
            async (db, token) =>
            {
                var order = await db.ProductionOrders
                    .Include(o => o.RoutingCenters)
                    .FirstOrDefaultAsync(o => o.Id == id, token);
                if (order is null)
                    return ApplicationResult<Func<ProductionOrder>>.NotFound();

                order.Status = ProductionOrderStatus.Cancelled;
                order.ModifiedUtc = DateTime.UtcNow;

                // A withdrawn order is not scheduled any more, so it must stop
                // holding its work centres hostage. The snapshot stays; only the
                // index of the live dependency goes.
                order.RoutingCenters.Clear();

                return ApplicationResult<Func<ProductionOrder>>.Success(() => order);
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Deletes an order outright. Only a draft may go, since it never reached the shop.</summary>
    public async Task<ApplicationResult<bool>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageOrders, cancellationToken))
            return ApplicationResult<bool>.Forbidden();

        return await DatabaseMutation.RunAsync<bool>(
            _db,
            async (db, token) =>
            {
                var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == id, token);
                if (order is null)
                    return ApplicationResult<Func<bool>>.NotFound();

                if (order.Status != ProductionOrderStatus.Draft)
                    return ApplicationResult<Func<bool>>.Conflict(
                        new ValidationIssue(nameof(ProductionOrder.Status), "Val_OrderNotDraft"));

                db.ProductionOrders.Remove(order);
                return ApplicationResult<Func<bool>>.Success(() => true);
            },
            cancellationToken: cancellationToken);
    }

    private static void Normalize(ProductionOrder order)
    {
        order.OrderNumber = Text.Key(order.OrderNumber);
        order.RoutingRevision = order.RoutingRevision?.Trim() ?? "";
        order.ReleaseLocal = PlantTime.Wall(order.ReleaseLocal);
        order.DueLocal = PlantTime.Wall(order.DueLocal);
    }
}
