using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

/// <summary>Production orders: the things that are actually scheduled.</summary>
public static class ProductionOrderEndpoints
{
    /// <summary>
    /// Maps the order routes, including the two lifecycle transitions.
    /// <para>
    /// Release is a POST to its own route rather than a status field on the
    /// update, because releasing is not an edit: it freezes the routing onto the
    /// order and is the moment the order stops depending on master data. Making
    /// it look like "set Status = 1" would invite a client to do it by accident.
    /// </para>
    /// </summary>
    /// <param name="app">The route builder to add to.</param>
    public static void MapProductionOrderEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/production-orders")
            .RequireAuthorization()
            .WithTags("Production orders");

        group.MapGet("/", async (ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var rows = await db.ProductionOrders
                .Include(o => o.WorkPlan)
                .AsNoTracking()
                .OrderByDescending(o => o.DueLocal)
                .Select(o => new { Order = o, Stamp = EF.Property<string>(o, ApiDbContext.ConcurrencyStamp) })
                .ToListAsync(cancellationToken);

            return TypedResults.Ok(rows.Select(r => r.Order.ToDto(null, r.Stamp)).ToList());
        })
        .WithSummary("Every production order, latest target date first.");

        group.MapGet("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var row = await db.ProductionOrders
                .Include(o => o.WorkPlan)
                .AsNoTracking()
                .Where(o => o.Id == id)
                .Select(o => new { Order = o, Stamp = EF.Property<string>(o, ApiDbContext.ConcurrencyStamp) })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null
                ? Problems.NotFound("production-order")
                : Results.Ok(row.Order.ToDto(null, row.Stamp));
        })
        .WithSummary("One production order.");

        group.MapPost("/", async (ProductionOrderDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var order = new ProductionOrder { CreatedUtc = DateTime.UtcNow };
            order.CopyFrom(request);

            var issues = ProductionOrderValidator.Validate(order);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (!await db.WorkPlans.AnyAsync(p => p.Id == order.WorkPlanId, cancellationToken))
                return Problems.Validation(nameof(ProductionOrderDto.WorkPlanId), "Val_WorkPlanMissing");

            if (await db.ProductionOrders.AnyAsync(o => o.OrderNumber == order.OrderNumber, cancellationToken))
                return Problems.Conflict(nameof(ProductionOrderDto.OrderNumber), "Val_OrderNumberTaken");

            order.ModifiedUtc = order.CreatedUtc;
            var entry = db.ProductionOrders.Add(order);
            ApiDbContext.Restamp(entry, expected: null);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/production-orders/{order.Id}", order.ToDto(null, ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageOrders)
        .WithSummary("Creates a draft production order.");

        group.MapPut("/{id:int}", async (int id, ProductionOrderDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConcurrencyStamp))
                return Problems.Validation(nameof(ProductionOrderDto.ConcurrencyStamp), "Val_Required");

            var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
            if (order is null)
                return Problems.NotFound("production-order");

            // Once released, the terms of the order are a record of what the shop
            // was told to build. Editing them afterwards is not an update, it is
            // a falsification.
            if (order.Status != ProductionOrderStatus.Draft)
                return Problems.Conflict(nameof(ProductionOrderDto.Status), "Val_OrderNotDraft");

            order.CopyFrom(request);

            var issues = ProductionOrderValidator.Validate(order);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (!await db.WorkPlans.AnyAsync(p => p.Id == order.WorkPlanId, cancellationToken))
                return Problems.Validation(nameof(ProductionOrderDto.WorkPlanId), "Val_WorkPlanMissing");

            if (await db.ProductionOrders.AnyAsync(o => o.OrderNumber == order.OrderNumber && o.Id != id, cancellationToken))
                return Problems.Conflict(nameof(ProductionOrderDto.OrderNumber), "Val_OrderNumberTaken");

            order.ModifiedUtc = DateTime.UtcNow;
            var entry = db.Entry(order);
            ApiDbContext.Restamp(entry, request.ConcurrencyStamp);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Problems.Concurrency("production-order");
            }

            return Results.Ok(order.ToDto(null, ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageOrders)
        .WithSummary("Replaces a draft production order. Requires the concurrency stamp that was read.");

        group.MapPost("/{id:int}/release", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var order = await db.ProductionOrders
                .Include(o => o.RoutingCenters)
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

            if (order is null)
                return Problems.NotFound("production-order");

            if (order.Status != ProductionOrderStatus.Draft)
                return Problems.Conflict(nameof(ProductionOrderDto.Status), "Val_OrderNotDraft");

            var plan = await db.WorkPlans
                .Include(p => p.Operations).ThenInclude(o => o.WorkCenter)
                .FirstOrDefaultAsync(p => p.Id == order.WorkPlanId, cancellationToken);

            if (plan is null)
                return Problems.Validation(nameof(ProductionOrderDto.WorkPlanId), "Val_WorkPlanMissing");

            if (plan.Operations.Count == 0)
                return Problems.Validation(nameof(ProductionOrderDto.WorkPlanId), "Val_OrderPlanHasNoOperations");

            var snapshot = RoutingSnapshot.Capture(plan);
            order.RoutingSnapshotJson = snapshot.Serialize();
            order.RoutingRevision = plan.Revision ?? "";
            order.Status = ProductionOrderStatus.Released;
            order.ModifiedUtc = DateTime.UtcNow;

            // Lift the work-centre ids out of the blob so the database can protect
            // them. Without these rows nothing stops a planner deleting a machine
            // a live shop-floor order depends on, and the order then disappears
            // from the schedule with only a preparation error to show for it.
            order.RoutingCenters.Clear();
            foreach (var workCenterId in snapshot.WorkCenterIds)
                order.RoutingCenters.Add(new OrderRoutingCenter { ProductionOrderId = order.Id, WorkCenterId = workCenterId });

            var entry = db.Entry(order);
            ApiDbContext.Restamp(entry, expected: null);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(order.ToDto(plan.PlanNumber, ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageOrders)
        .WithSummary("Freezes the current routing onto the order and releases it.");

        group.MapPost("/{id:int}/cancel", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var order = await db.ProductionOrders
                .Include(o => o.RoutingCenters)
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

            if (order is null)
                return Problems.NotFound("production-order");

            order.Status = ProductionOrderStatus.Cancelled;
            order.ModifiedUtc = DateTime.UtcNow;

            // The dependency is over, so the rows that recorded it go. The
            // snapshot stays: it is the record of what the shop was told to build.
            order.RoutingCenters.Clear();

            var entry = db.Entry(order);
            ApiDbContext.Restamp(entry, expected: null);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(order.ToDto(null, ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageOrders)
        .WithSummary("Withdraws an order. The snapshot is kept as the record of what was released.");

        group.MapDelete("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var order = await db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
            if (order is null)
                return Problems.NotFound("production-order");

            if (order.Status != ProductionOrderStatus.Draft)
                return Problems.Conflict(nameof(ProductionOrderDto.Status), "Val_OrderNotDraft");

            db.ProductionOrders.Remove(order);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization(PolicyNames.ManageOrders)
        .WithSummary("Deletes a draft order; only a draft never reached the shop.");
    }
}
