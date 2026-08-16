using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>
/// Production orders and the one operation that matters: release, which freezes
/// the routing.
/// </summary>
public sealed class ProductionOrderService
{
    private readonly BrowserDatabase _db;

    public ProductionOrderService(BrowserDatabase db) => _db = db;

    public async Task<List<ProductionOrder>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.ProductionOrders
            .Include(o => o.WorkPlan)
            .AsNoTracking()
            .OrderByDescending(o => o.DueUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Orders whose routing is frozen and which therefore can be scheduled.</summary>
    public async Task<List<ProductionOrder>> GetSchedulableAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.ProductionOrders
            .AsNoTracking()
            .Where(o => o.Status == ProductionOrderStatus.Released && o.RoutingSnapshotJson != "")
            .OrderBy(o => o.DueUtc)
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
        var numbers = await db.ProductionOrders.Select(o => o.OrderNumber).ToListAsync(cancellationToken);

        int highest = numbers
            .Select(n => int.TryParse(n.Replace("PO-", "", StringComparison.Ordinal), out var value) ? value : 0)
            .DefaultIfEmpty(1000)
            .Max();

        return $"PO-{highest + 1}";
    }

    public async Task<ApplicationResult<ProductionOrder>> SaveAsync(
        ProductionOrder order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var issues = ProductionOrderValidator.Validate(order);
        if (issues.Count > 0)
            return ApplicationResult<ProductionOrder>.Validation(issues);

        await using var db = await _db.CreateContextAsync(cancellationToken);

        if (!await db.WorkPlans.AnyAsync(p => p.Id == order.WorkPlanId, cancellationToken))
            return ApplicationResult<ProductionOrder>.Validation(
                [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_WorkPlanMissing")]);

        var trimmed = order.OrderNumber.Trim();
        if (await db.ProductionOrders.AnyAsync(o => o.OrderNumber == trimmed && o.Id != order.Id, cancellationToken))
            return ApplicationResult<ProductionOrder>.Conflict(
                new ValidationIssue(nameof(ProductionOrder.OrderNumber), "Val_OrderNumberTaken"));

        ProductionOrder entity;
        if (order.Id == 0)
        {
            entity = new ProductionOrder { CreatedUtc = DateTime.UtcNow };
            db.ProductionOrders.Add(entity);
        }
        else
        {
            var existing = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == order.Id, cancellationToken);
            if (existing is null)
                return ApplicationResult<ProductionOrder>.NotFound();

            // The snapshot is the whole point: once frozen, the terms of the order
            // are a record of what the shop was told to build.
            if (existing.Status != ProductionOrderStatus.Draft)
                return ApplicationResult<ProductionOrder>.Conflict(
                    new ValidationIssue(nameof(ProductionOrder.Status), "Val_OrderNotDraft"));

            entity = existing;
        }

        entity.OrderNumber = trimmed;
        entity.WorkPlanId = order.WorkPlanId;
        entity.Quantity = order.Quantity;
        entity.ReleaseUtc = order.ReleaseUtc;
        entity.DueUtc = order.DueUtc;
        entity.Priority = order.Priority;
        entity.ModifiedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return await PersistAsync(entity, cancellationToken);
    }

    /// <summary>
    /// Freezes the current routing onto the order and releases it. This is the
    /// moment the order stops depending on master data.
    /// </summary>
    public async Task<ApplicationResult<ProductionOrder>> ReleaseAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);

        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
            return ApplicationResult<ProductionOrder>.NotFound();

        if (order.Status != ProductionOrderStatus.Draft)
            return ApplicationResult<ProductionOrder>.Conflict(
                new ValidationIssue(nameof(ProductionOrder.Status), "Val_OrderNotDraft"));

        var plan = await db.WorkPlans
            .Include(p => p.Operations).ThenInclude(o => o.WorkCenter)
            .FirstOrDefaultAsync(p => p.Id == order.WorkPlanId, cancellationToken);

        if (plan is null)
            return ApplicationResult<ProductionOrder>.Validation(
                [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_WorkPlanMissing")]);

        if (plan.Operations.Count == 0)
            return ApplicationResult<ProductionOrder>.Validation(
                [new ValidationIssue(nameof(ProductionOrder.WorkPlanId), "Val_OrderPlanHasNoOperations")]);

        order.RoutingSnapshotJson = RoutingSnapshot.Capture(plan).Serialize();
        order.RoutingRevision = plan.Revision ?? "";
        order.Status = ProductionOrderStatus.Released;
        order.ModifiedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return await PersistAsync(order, cancellationToken);
    }

    /// <summary>Withdraws an order. The snapshot is kept as the record of what was released.</summary>
    public async Task<ApplicationResult<ProductionOrder>> CancelAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);

        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
            return ApplicationResult<ProductionOrder>.NotFound();

        order.Status = ProductionOrderStatus.Cancelled;
        order.ModifiedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return await PersistAsync(order, cancellationToken);
    }

    /// <summary>Deletes an order outright. Only a draft may go, since it never reached the shop.</summary>
    public async Task<ApplicationResult<bool>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);

        var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
            return ApplicationResult<bool>.NotFound();

        if (order.Status != ProductionOrderStatus.Draft)
            return ApplicationResult<bool>.Conflict(
                new ValidationIssue(nameof(ProductionOrder.Status), "Val_OrderNotDraft"));

        db.ProductionOrders.Remove(order);
        await db.SaveChangesAsync(cancellationToken);

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<bool>.Success(true)
            : ApplicationResult<bool>.PersistenceFailed();
    }

    private async Task<ApplicationResult<ProductionOrder>> PersistAsync(
        ProductionOrder order,
        CancellationToken cancellationToken)
    {
        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<ProductionOrder>.Success(order)
            : ApplicationResult<ProductionOrder>.PersistenceFailed();
    }
}
