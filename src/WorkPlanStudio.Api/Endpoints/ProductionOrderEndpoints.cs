using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Security;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Endpoints;

public static class ProductionOrderEndpoints
{
    public static IEndpointRouteBuilder MapProductionOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/production-orders").RequireAuthorization("operator").RequireRateLimiting("api");
        group.MapGet("/", GetAllAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAllAsync(ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var orders = await db.ProductionOrders.AsNoTracking().Include(order => order.WorkPlan)
            .Where(order => order.OwnerId == ownerId).OrderBy(order => order.DueUtc)
            .ThenByDescending(order => order.Priority).ToListAsync(cancellationToken);
        return Results.Ok(orders.Select(order => order.ToDto()));
    }

    private static async Task<IResult> GetAsync(int id, ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var order = await db.ProductionOrders.AsNoTracking().Include(item => item.WorkPlan)
            .FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(order.ToDto());
    }

    private static async Task<IResult> CreateAsync(
        ProductionOrderDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var error = Validate(request);
        if (error is not null)
            return Results.BadRequest(error);
        if (await db.ProductionOrders.AnyAsync(order => order.OwnerId == ownerId && order.OrderNumber == request.OrderNumber.Trim(), cancellationToken))
            return Results.Conflict(new ApiError("order_number_conflict", "A production order with that number already exists."));
        var plan = await db.WorkPlans.AsNoTracking().Include(item => item.Operations)
            .FirstOrDefaultAsync(item => item.Id == request.WorkPlanId && item.OwnerId == ownerId, cancellationToken);
        if (plan is null || plan.Status != WorkPlanStatus.Released)
            return Results.BadRequest(new ApiError("routing_not_released", "A released work plan is required."));

        var now = DateTime.UtcNow;
        var order = new ProductionOrder
        {
            OwnerId = ownerId,
            OrderNumber = request.OrderNumber.Trim(),
            WorkPlanId = plan.Id,
            Quantity = request.Quantity,
            ReleaseUtc = request.ReleaseUtc.ToUniversalTime(),
            DueUtc = request.DueUtc.ToUniversalTime(),
            Priority = request.Priority,
            Status = request.Status,
            RoutingRevision = plan.Revision ?? "1",
            RoutingSnapshotJson = JsonSerializer.Serialize(plan.ToDto()),
            CreatedUtc = now,
            ModifiedUtc = now
        };
        db.ProductionOrders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await EndpointSupport.AuditAsync(db, principal, context, "create", order, request, cancellationToken);
        order.WorkPlan = plan;
        return Results.Created($"/api/production-orders/{order.Id}", order.ToDto());
    }

    private static async Task<IResult> UpdateAsync(
        int id, ProductionOrderDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var error = Validate(request);
        if (error is not null)
            return Results.BadRequest(error);
        var order = await db.ProductionOrders.Include(item => item.WorkPlan)
            .FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (order is null)
            return Results.NotFound();
        if (!ValidTransition(order.Status, request.Status))
            return Results.Conflict(new ApiError("invalid_status_transition", "The requested order status transition is not allowed."));
        if (order.Status != ProductionOrderStatus.Draft &&
            (request.WorkPlanId != order.WorkPlanId || request.Quantity != order.Quantity))
            return Results.Conflict(new ApiError("released_order_immutable", "Routing and quantity are immutable after release."));
        if (await db.ProductionOrders.AnyAsync(item => item.OwnerId == ownerId &&
                item.OrderNumber == request.OrderNumber.Trim() && item.Id != id, cancellationToken))
            return Results.Conflict(new ApiError("order_number_conflict", "A production order with that number already exists."));

        if (order.Status == ProductionOrderStatus.Draft && request.WorkPlanId != order.WorkPlanId)
        {
            var plan = await db.WorkPlans.AsNoTracking().Include(item => item.Operations)
                .FirstOrDefaultAsync(item => item.Id == request.WorkPlanId && item.OwnerId == ownerId && item.Status == WorkPlanStatus.Released, cancellationToken);
            if (plan is null)
                return Results.BadRequest(new ApiError("routing_not_released", "A released work plan is required."));
            order.WorkPlanId = plan.Id;
            order.WorkPlan = plan;
            order.RoutingRevision = plan.Revision ?? "1";
            order.RoutingSnapshotJson = JsonSerializer.Serialize(plan.ToDto());
        }

        db.Entry(order).Property(item => item.Version).OriginalValue = request.Version;
        order.OrderNumber = request.OrderNumber.Trim();
        order.Quantity = request.Quantity;
        order.ReleaseUtc = request.ReleaseUtc.ToUniversalTime();
        order.DueUtc = request.DueUtc.ToUniversalTime();
        order.Priority = request.Priority;
        order.Status = request.Status;
        order.ModifiedUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("concurrency_conflict", "The production order changed since it was loaded."));
        }
        await EndpointSupport.AuditAsync(db, principal, context, "update", order, request, cancellationToken);
        return Results.Ok(order.ToDto());
    }

    private static async Task<IResult> DeleteAsync(
        int id, long version, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var order = await db.ProductionOrders.FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (order is null)
            return Results.NotFound();
        if (order.Status != ProductionOrderStatus.Draft && order.Status != ProductionOrderStatus.Cancelled)
            return Results.Conflict(new ApiError("order_not_deletable", "Only draft or cancelled orders can be deleted."));
        db.Entry(order).Property(item => item.Version).OriginalValue = version;
        db.ProductionOrders.Remove(order);
        EndpointSupport.AddAudit(db, principal, context, "delete", order, null);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("concurrency_conflict", "The production order changed since it was loaded."));
        }
        return Results.NoContent();
    }

    private static ApiError? Validate(ProductionOrderDto order)
    {
        if (string.IsNullOrWhiteSpace(order.OrderNumber) || order.OrderNumber.Trim().Length > 30)
            return new("invalid_order_number", "Order number is required and limited to 30 characters.");
        if (order.Quantity is < 1 or > 1_000_000)
            return new("invalid_quantity", "Quantity must be between 1 and 1,000,000.");
        if (order.Priority is < 1 or > 10)
            return new("invalid_priority", "Priority must be between 1 and 10.");
        if (order.DueUtc <= order.ReleaseUtc)
            return new("invalid_dates", "Due date must be later than release date.");
        return !Enum.IsDefined(order.Status)
            ? new("invalid_status", "Production order status is invalid.")
            : null;
    }

    private static bool ValidTransition(ProductionOrderStatus from, ProductionOrderStatus to) => from == to || (from, to) switch
    {
        (ProductionOrderStatus.Draft, ProductionOrderStatus.Released) => true,
        (ProductionOrderStatus.Draft, ProductionOrderStatus.Cancelled) => true,
        (ProductionOrderStatus.Released, ProductionOrderStatus.Scheduled) => true,
        (ProductionOrderStatus.Released, ProductionOrderStatus.Cancelled) => true,
        (ProductionOrderStatus.Scheduled, ProductionOrderStatus.InProgress) => true,
        (ProductionOrderStatus.Scheduled, ProductionOrderStatus.Cancelled) => true,
        (ProductionOrderStatus.InProgress, ProductionOrderStatus.Completed) => true,
        _ => false
    };
}
