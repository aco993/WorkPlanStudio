using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Security;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

public static class WorkCenterEndpoints
{
    public static IEndpointRouteBuilder MapWorkCenterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/work-centers").RequireAuthorization("operator").RequireRateLimiting("api");
        group.MapGet("/", GetAllAsync);
        group.MapGet("/usage-counts", GetUsageCountsAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> GetUsageCountsAsync(ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var counts = await db.Operations.AsNoTracking()
            .Where(operation => operation.WorkPlan!.OwnerId == ownerId)
            .GroupBy(operation => operation.WorkCenterId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken);
        return Results.Ok(counts);
    }

    private static async Task<IResult> GetAllAsync(ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var centers = await db.WorkCenters.AsNoTracking().Where(center => center.OwnerId == ownerId)
            .OrderBy(center => center.Code).Select(center => center.ToDto()).ToListAsync(cancellationToken);
        return Results.Ok(centers);
    }

    private static async Task<IResult> GetAsync(int id, ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var center = await db.WorkCenters.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        return center is null ? Results.NotFound() : Results.Ok(center.ToDto());
    }

    private static async Task<IResult> CreateAsync(
        WorkCenterDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var center = Map(request, ownerId);
        var issues = WorkCenterValidator.Validate(center);
        if (issues.Count > 0)
            return EndpointSupport.ValidationProblem(issues);
        if (!ValidTimeZone(center.TimeZoneId))
            return Results.BadRequest(new ApiError("invalid_time_zone", "Time zone identifier is not supported by this server."));
        if (await db.WorkCenters.AnyAsync(item => item.OwnerId == ownerId && item.Code == center.Code, cancellationToken))
            return Results.Conflict(new ApiError("code_conflict", "A work center with that code already exists."));
        db.WorkCenters.Add(center);
        await db.SaveChangesAsync(cancellationToken);
        await EndpointSupport.AuditAsync(db, principal, context, "create", center, request, cancellationToken);
        return Results.Created($"/api/work-centers/{center.Id}", center.ToDto());
    }

    private static async Task<IResult> UpdateAsync(
        int id, WorkCenterDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var center = await db.WorkCenters.FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (center is null)
            return Results.NotFound();
        var candidate = Map(request, ownerId);
        var issues = WorkCenterValidator.Validate(candidate);
        if (issues.Count > 0)
            return EndpointSupport.ValidationProblem(issues);
        if (!ValidTimeZone(candidate.TimeZoneId))
            return Results.BadRequest(new ApiError("invalid_time_zone", "Time zone identifier is not supported by this server."));
        if (await db.WorkCenters.AnyAsync(item => item.OwnerId == ownerId && item.Code == candidate.Code && item.Id != id, cancellationToken))
            return Results.Conflict(new ApiError("code_conflict", "A work center with that code already exists."));
        if (center.IsActive && !candidate.IsActive && await db.Operations.AnyAsync(
                operation => operation.WorkCenterId == id && operation.WorkPlan!.OwnerId == ownerId &&
                             operation.WorkPlan.Status == WorkPlanStatus.Released, cancellationToken))
            return Results.Conflict(new ApiError("released_routing_uses_center", "Released routing operations use this work center."));

        db.Entry(center).Property(item => item.Version).OriginalValue = request.Version;
        center.Code = candidate.Code;
        center.Name = candidate.Name;
        center.CostCenter = candidate.CostCenter;
        center.HourlyRate = candidate.HourlyRate;
        center.ParallelCapacity = candidate.ParallelCapacity;
        center.TimeZoneId = candidate.TimeZoneId;
        center.IsActive = candidate.IsActive;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("concurrency_conflict", "The work center changed since it was loaded."));
        }
        await EndpointSupport.AuditAsync(db, principal, context, "update", center, request, cancellationToken);
        return Results.Ok(center.ToDto());
    }

    private static async Task<IResult> DeleteAsync(
        int id, long version, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var center = await db.WorkCenters.FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (center is null)
            return Results.NotFound();
        if (await db.Operations.AnyAsync(operation => operation.WorkCenterId == id && operation.WorkPlan!.OwnerId == ownerId, cancellationToken))
            return Results.Conflict(new ApiError("work_center_in_use", "The work center is referenced by routing operations."));
        db.Entry(center).Property(item => item.Version).OriginalValue = version;
        await EndpointSupport.AuditAsync(db, principal, context, "delete", center, null, cancellationToken);
        db.WorkCenters.Remove(center);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("concurrency_conflict", "The work center changed since it was loaded."));
        }
        return Results.NoContent();
    }

    private static WorkCenter Map(WorkCenterDto request, string ownerId) => new()
    {
        OwnerId = ownerId,
        Code = request.Code?.Trim() ?? "",
        Name = request.Name?.Trim() ?? "",
        CostCenter = request.CostCenter?.Trim() ?? "",
        HourlyRate = request.HourlyRate,
        ParallelCapacity = request.ParallelCapacity,
        TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "UTC" : request.TimeZoneId.Trim(),
        IsActive = request.IsActive,
        Version = request.Version
    };

    private static bool ValidTimeZone(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
