using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Security;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

public static class WorkPlanEndpoints
{
    public static IEndpointRouteBuilder MapWorkPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/work-plans").RequireAuthorization("operator").RequireRateLimiting("api");
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
        var plans = await db.WorkPlans.AsNoTracking().Include(plan => plan.Operations)
            .Where(plan => plan.OwnerId == ownerId).OrderBy(plan => plan.PlanNumber)
            .ToListAsync(cancellationToken);
        return Results.Ok(plans.Select(plan => plan.ToDto()));
    }

    private static async Task<IResult> GetAsync(int id, ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var plan = await db.WorkPlans.AsNoTracking().Include(item => item.Operations)
            .FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        return plan is null ? Results.NotFound() : Results.Ok(plan.ToDto());
    }

    private static async Task<IResult> CreateAsync(
        WorkPlanDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var plan = Map(request, ownerId);
        var centers = await EndpointSupport.OwnedCentersAsync(db, ownerId, cancellationToken);
        var issues = WorkPlanValidator.Validate(plan, centers);
        if (issues.Count > 0)
            return EndpointSupport.ValidationProblem(issues);
        if (await db.WorkPlans.AnyAsync(item => item.OwnerId == ownerId && item.PlanNumber == plan.PlanNumber, cancellationToken))
            return Results.Conflict(new ApiError("plan_number_conflict", "A work plan with that number already exists."));
        plan.CreatedUtc = plan.ModifiedUtc = DateTime.UtcNow;
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await EndpointSupport.AuditAsync(db, principal, context, "create", plan, request, cancellationToken);
        return Results.Created($"/api/work-plans/{plan.Id}", plan.ToDto());
    }

    private static async Task<IResult> UpdateAsync(
        int id, WorkPlanDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var existing = await db.WorkPlans.Include(plan => plan.Operations)
            .FirstOrDefaultAsync(plan => plan.Id == id && plan.OwnerId == ownerId, cancellationToken);
        if (existing is null)
            return Results.NotFound();
        var candidate = Map(request, ownerId);
        var centers = await EndpointSupport.OwnedCentersAsync(db, ownerId, cancellationToken);
        var issues = WorkPlanValidator.Validate(candidate, centers, existing.Status);
        if (issues.Count > 0)
            return EndpointSupport.ValidationProblem(issues);
        if (await db.WorkPlans.AnyAsync(plan => plan.OwnerId == ownerId && plan.PlanNumber == candidate.PlanNumber && plan.Id != id, cancellationToken))
            return Results.Conflict(new ApiError("plan_number_conflict", "A work plan with that number already exists."));
        if (existing.Status != WorkPlanStatus.Draft && await db.ProductionOrders.AnyAsync(order =>
                order.OwnerId == ownerId && order.WorkPlanId == id && order.Status != ProductionOrderStatus.Draft, cancellationToken))
            return Results.Conflict(new ApiError("routing_in_use", "A released production order uses this routing revision."));

        db.Entry(existing).Property(plan => plan.Version).OriginalValue = request.Version;
        existing.PlanNumber = candidate.PlanNumber;
        existing.PartNumber = candidate.PartNumber;
        existing.PartName = candidate.PartName;
        existing.Revision = candidate.Revision;
        existing.Status = candidate.Status;
        existing.LotSize = candidate.LotSize;
        existing.ModifiedUtc = DateTime.UtcNow;
        db.Operations.RemoveRange(existing.Operations);
        existing.Operations = candidate.Operations;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("concurrency_conflict", "The work plan changed since it was loaded."));
        }
        await EndpointSupport.AuditAsync(db, principal, context, "update", existing, request, cancellationToken);
        return Results.Ok(existing.ToDto());
    }

    private static async Task<IResult> DeleteAsync(
        int id, long version, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var plan = await db.WorkPlans.FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (plan is null)
            return Results.NotFound();
        if (await db.ProductionOrders.AnyAsync(order => order.OwnerId == ownerId && order.WorkPlanId == id, cancellationToken))
            return Results.Conflict(new ApiError("routing_in_use", "Production orders reference this work plan."));
        db.Entry(plan).Property(item => item.Version).OriginalValue = version;
        await EndpointSupport.AuditAsync(db, principal, context, "delete", plan, null, cancellationToken);
        db.WorkPlans.Remove(plan);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("concurrency_conflict", "The work plan changed since it was loaded."));
        }
        return Results.NoContent();
    }

    private static WorkPlan Map(WorkPlanDto request, string ownerId) => new()
    {
        OwnerId = ownerId,
        PlanNumber = request.PlanNumber?.Trim() ?? "",
        PartNumber = request.PartNumber?.Trim() ?? "",
        PartName = request.PartName?.Trim() ?? "",
        Revision = string.IsNullOrWhiteSpace(request.Revision) ? null : request.Revision.Trim(),
        Status = request.Status,
        LotSize = request.LotSize,
        Version = request.Version,
        Operations = request.Operations.Select(operation => new Operation
        {
            OperationNumber = operation.OperationNumber,
            Description = operation.Description?.Trim() ?? "",
            WorkCenterId = operation.WorkCenterId,
            SetupTimeMinutes = operation.SetupTimeMinutes,
            TimePerPieceMinutes = operation.TimePerPieceMinutes,
            SetupFamily = string.IsNullOrWhiteSpace(operation.SetupFamily) ? "DEFAULT" : operation.SetupFamily.Trim(),
            Remarks = string.IsNullOrWhiteSpace(operation.Remarks) ? null : operation.Remarks.Trim()
        }).ToList()
    };
}
