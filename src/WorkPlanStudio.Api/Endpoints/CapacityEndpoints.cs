using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Security;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Endpoints;

public static class CapacityEndpoints
{
    public static IEndpointRouteBuilder MapCapacityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/capacity").RequireAuthorization("operator").RequireRateLimiting("api");
        group.MapGet("/work-centers/{workCenterId:int}", GetAsync);
        group.MapPost("/shifts", SaveShiftAsync);
        group.MapDelete("/shifts/{id:int}", DeleteShiftAsync);
        group.MapPost("/downtimes", SaveDowntimeAsync);
        group.MapDelete("/downtimes/{id:int}", DeleteDowntimeAsync);
        group.MapPost("/setup-transitions", SaveSetupAsync);
        group.MapDelete("/setup-transitions/{id:int}", DeleteSetupAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        int workCenterId, ProductionDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var ownerId = principal.RequiredUserId();
        if (!await OwnsCenterAsync(db, ownerId, workCenterId, cancellationToken))
            return Results.NotFound();
        var shifts = await db.CalendarShifts.AsNoTracking().Where(item => item.WorkCenterId == workCenterId)
            .OrderBy(item => item.DayOfWeek).ThenBy(item => item.StartMinute)
            .Select(item => new CalendarShiftDto(item.Id, item.WorkCenterId, item.DayOfWeek, item.StartMinute, item.EndMinute))
            .ToListAsync(cancellationToken);
        var downtimes = await db.MachineDowntimes.AsNoTracking().Where(item => item.WorkCenterId == workCenterId)
            .OrderBy(item => item.StartUtc)
            .Select(item => new MachineDowntimeDto(item.Id, item.WorkCenterId, item.StartUtc, item.EndUtc, item.Reason))
            .ToListAsync(cancellationToken);
        var setups = await db.SetupTransitions.AsNoTracking().Where(item => item.WorkCenterId == workCenterId)
            .OrderBy(item => item.FromFamily).ThenBy(item => item.ToFamily)
            .Select(item => new SetupTransitionDto(item.Id, item.WorkCenterId, item.FromFamily, item.ToFamily, item.DurationMinutes))
            .ToListAsync(cancellationToken);
        return Results.Ok(new CapacityProfileDto(shifts, downtimes, setups));
    }

    private static async Task<IResult> SaveShiftAsync(
        CalendarShiftDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        if (!await OwnsCenterAsync(db, ownerId, request.WorkCenterId, cancellationToken))
            return Results.NotFound();
        if (request.StartMinute < 0 || request.EndMinute > 1440 || request.EndMinute <= request.StartMinute)
            return Results.BadRequest(new ApiError("invalid_shift", "Shift minutes must form a non-empty range inside one day."));
        if (await db.CalendarShifts.AnyAsync(item => item.WorkCenterId == request.WorkCenterId &&
                item.DayOfWeek == request.DayOfWeek && item.Id != request.Id &&
                item.StartMinute < request.EndMinute && request.StartMinute < item.EndMinute, cancellationToken))
            return Results.Conflict(new ApiError("overlapping_shift", "Calendar shifts cannot overlap."));
        var shift = request.Id == 0
            ? new CalendarShift { WorkCenterId = request.WorkCenterId }
            : await db.CalendarShifts.FirstOrDefaultAsync(item => item.Id == request.Id && item.WorkCenterId == request.WorkCenterId, cancellationToken);
        if (shift is null)
            return Results.NotFound();
        shift.DayOfWeek = request.DayOfWeek;
        shift.StartMinute = request.StartMinute;
        shift.EndMinute = request.EndMinute;
        if (shift.Id == 0)
            db.CalendarShifts.Add(shift);
        await db.SaveChangesAsync(cancellationToken);
        await EndpointSupport.AuditAsync(db, principal, context, request.Id == 0 ? "create" : "update", shift, request, cancellationToken);
        return Results.Ok(new CalendarShiftDto(shift.Id, shift.WorkCenterId, shift.DayOfWeek, shift.StartMinute, shift.EndMinute));
    }

    private static async Task<IResult> SaveDowntimeAsync(
        MachineDowntimeDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        if (!await OwnsCenterAsync(db, ownerId, request.WorkCenterId, cancellationToken))
            return Results.NotFound();
        if (request.EndUtc <= request.StartUtc || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 200)
            return Results.BadRequest(new ApiError("invalid_downtime", "Downtime needs a valid range and reason."));
        if (await db.MachineDowntimes.AnyAsync(item => item.WorkCenterId == request.WorkCenterId && item.Id != request.Id &&
                item.StartUtc < request.EndUtc && request.StartUtc < item.EndUtc, cancellationToken))
            return Results.Conflict(new ApiError("overlapping_downtime", "Downtime windows cannot overlap."));
        var downtime = request.Id == 0
            ? new MachineDowntime { WorkCenterId = request.WorkCenterId }
            : await db.MachineDowntimes.FirstOrDefaultAsync(item => item.Id == request.Id && item.WorkCenterId == request.WorkCenterId, cancellationToken);
        if (downtime is null)
            return Results.NotFound();
        downtime.StartUtc = request.StartUtc.ToUniversalTime();
        downtime.EndUtc = request.EndUtc.ToUniversalTime();
        downtime.Reason = request.Reason.Trim();
        if (downtime.Id == 0)
            db.MachineDowntimes.Add(downtime);
        await db.SaveChangesAsync(cancellationToken);
        await EndpointSupport.AuditAsync(db, principal, context, request.Id == 0 ? "create" : "update", downtime, request, cancellationToken);
        return Results.Ok(new MachineDowntimeDto(downtime.Id, downtime.WorkCenterId, downtime.StartUtc, downtime.EndUtc, downtime.Reason));
    }

    private static async Task<IResult> SaveSetupAsync(
        SetupTransitionDto request, ProductionDbContext db, ClaimsPrincipal principal,
        IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken)
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var ownerId = principal.RequiredUserId();
        if (!await OwnsCenterAsync(db, ownerId, request.WorkCenterId, cancellationToken))
            return Results.NotFound();
        var from = request.FromFamily?.Trim() ?? "";
        var to = request.ToFamily?.Trim() ?? "";
        if (from.Length is < 1 or > 40 || to.Length is < 1 or > 40 || request.DurationMinutes is < 0 or > 10080)
            return Results.BadRequest(new ApiError("invalid_setup_transition", "Setup families and duration are outside their allowed range."));
        if (await db.SetupTransitions.AnyAsync(item => item.WorkCenterId == request.WorkCenterId && item.Id != request.Id &&
                item.FromFamily == from && item.ToFamily == to, cancellationToken))
            return Results.Conflict(new ApiError("setup_transition_conflict", "That setup transition already exists."));
        var setup = request.Id == 0
            ? new SetupTransition { WorkCenterId = request.WorkCenterId }
            : await db.SetupTransitions.FirstOrDefaultAsync(item => item.Id == request.Id && item.WorkCenterId == request.WorkCenterId, cancellationToken);
        if (setup is null)
            return Results.NotFound();
        setup.FromFamily = from;
        setup.ToFamily = to;
        setup.DurationMinutes = request.DurationMinutes;
        if (setup.Id == 0)
            db.SetupTransitions.Add(setup);
        await db.SaveChangesAsync(cancellationToken);
        await EndpointSupport.AuditAsync(db, principal, context, request.Id == 0 ? "create" : "update", setup, request, cancellationToken);
        return Results.Ok(new SetupTransitionDto(setup.Id, setup.WorkCenterId, setup.FromFamily, setup.ToFamily, setup.DurationMinutes));
    }

    private static Task<IResult> DeleteShiftAsync(int id, ProductionDbContext db, ClaimsPrincipal principal, IAntiforgery antiforgery, HttpContext context, CancellationToken token) =>
        DeleteOwnedAsync(id, db.CalendarShifts, item => item.WorkCenterId, db, principal, antiforgery, context, token);
    private static Task<IResult> DeleteDowntimeAsync(int id, ProductionDbContext db, ClaimsPrincipal principal, IAntiforgery antiforgery, HttpContext context, CancellationToken token) =>
        DeleteOwnedAsync(id, db.MachineDowntimes, item => item.WorkCenterId, db, principal, antiforgery, context, token);
    private static Task<IResult> DeleteSetupAsync(int id, ProductionDbContext db, ClaimsPrincipal principal, IAntiforgery antiforgery, HttpContext context, CancellationToken token) =>
        DeleteOwnedAsync(id, db.SetupTransitions, item => item.WorkCenterId, db, principal, antiforgery, context, token);

    private static async Task<IResult> DeleteOwnedAsync<TEntity>(
        int id, DbSet<TEntity> set, Func<TEntity, int> centerId,
        ProductionDbContext db, ClaimsPrincipal principal, IAntiforgery antiforgery,
        HttpContext context, CancellationToken cancellationToken) where TEntity : class
    {
        await EndpointSupport.ValidateAntiforgeryAsync(antiforgery, context);
        var entity = await set.FindAsync([id], cancellationToken);
        if (entity is null || !await OwnsCenterAsync(db, principal.RequiredUserId(), centerId(entity), cancellationToken))
            return Results.NotFound();
        await EndpointSupport.AuditAsync(db, principal, context, "delete", entity, null, cancellationToken);
        set.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static Task<bool> OwnsCenterAsync(ProductionDbContext db, string ownerId, int workCenterId, CancellationToken cancellationToken) =>
        db.WorkCenters.AnyAsync(center => center.Id == workCenterId && center.OwnerId == ownerId, cancellationToken);
}
