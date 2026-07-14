using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Scheduling;
using WorkPlanStudio.Api.Security;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Api.Endpoints;

public static class ScheduleRunEndpoints
{
    public static IEndpointRouteBuilder MapScheduleRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/schedule-runs").RequireAuthorization("operator").RequireRateLimiting("api");
        group.MapGet("/", GetAllAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAllAsync(ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var runs = await db.ScheduleRuns.AsNoTracking().Where(run => run.OwnerId == ownerId)
            .OrderByDescending(run => run.CreatedUtc).Take(100).ToListAsync(cancellationToken);
        return Results.Ok(runs.Select(run => run.ToDto()));
    }

    private static async Task<IResult> GetAsync(Guid id, ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        var run = await db.ScheduleRuns.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        return run is null ? Results.NotFound() : Results.Ok(run.ToDto());
    }

    private static async Task<IResult> CreateAsync(
        CreateScheduleRunRequest request, ProductionDbContext db, ScheduleRunQueue queue,
        ClaimsPrincipal principal, IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ids = request.ProductionOrderIds.Distinct().ToList();
        if (ids.Count is < 1 or > 500)
            return Results.BadRequest(new ApiError("invalid_order_count", "A schedule run needs between 1 and 500 distinct orders."));
        try
        {
            SchedulingParameterLimits.Validate(new SchedulingParameters
            {
                MultiStartRuns = request.MultiStartRuns,
                LocalSearchMaxSteps = request.LocalSearchMaxSteps,
                Seed = request.Seed
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new ApiError("invalid_scheduling_parameters", "Scheduling parameters are outside supported limits."));
        }

        var ownerId = principal.RequiredUserId();
        var count = await db.ProductionOrders.CountAsync(order => ownerId == order.OwnerId && ids.Contains(order.Id) &&
            (order.Status == ProductionOrderStatus.Released || order.Status == ProductionOrderStatus.Scheduled), cancellationToken);
        if (count != ids.Count)
            return Results.BadRequest(new ApiError("orders_not_schedulable", "Every selected order must exist and be released."));
        var run = new ScheduleRun
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Status = ScheduleRunStatus.Queued,
            ParametersJson = JsonSerializer.Serialize(request with { ProductionOrderIds = ids }),
            CreatedUtc = DateTime.UtcNow
        };
        db.ScheduleRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        _ = queue.TryQueue(run.Id); // Durable DB polling is the fallback when the local wake-up channel is full.
        await EndpointSupport.AuditAsync(db, principal, context, "queue", run, request, cancellationToken);
        return Results.Accepted($"/api/schedule-runs/{run.Id}", run.ToDto());
    }

    private static async Task<IResult> CancelAsync(
        Guid id, ProductionDbContext db, ScheduleRunQueue queue, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        var run = await db.ScheduleRuns.FirstOrDefaultAsync(item => item.Id == id && item.OwnerId == ownerId, cancellationToken);
        if (run is null)
            return Results.NotFound();
        if (run.Status is ScheduleRunStatus.Completed or ScheduleRunStatus.Failed or ScheduleRunStatus.Cancelled)
            return Results.Conflict(new ApiError("schedule_run_finished", "The schedule run is already finished."));
        queue.Cancel(id);
        run.CancellationRequestedUtc = DateTime.UtcNow;
        if (run.Status == ScheduleRunStatus.Queued)
        {
            run.Status = ScheduleRunStatus.Cancelled;
            run.ErrorCode = "cancelled";
            run.CompletedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await EndpointSupport.AuditAsync(db, principal, context, "cancel", run, null, cancellationToken);
        return Results.Accepted($"/api/schedule-runs/{id}", run.ToDto());
    }
}
