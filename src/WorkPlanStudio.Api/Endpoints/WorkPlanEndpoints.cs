using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

/// <summary>Work plans (routings) and the operations they are made of.</summary>
public static class WorkPlanEndpoints
{
    /// <summary>
    /// Maps the work-plan routes. A plan is written as a whole — header plus the
    /// complete operation list — because a routing is only meaningful as a
    /// sequence, and partial updates of a sequence are where off-by-one bugs live.
    /// </summary>
    /// <param name="app">The route builder to add to.</param>
    public static void MapWorkPlanEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/work-plans")
            .RequireAuthorization()
            .WithTags("Work plans");

        group.MapGet("/", async (ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var rows = await db.WorkPlans
                .Include(p => p.Operations).ThenInclude(o => o.WorkCenter)
                .AsNoTracking()
                .OrderBy(p => p.PlanNumber)
                .Select(p => new { Plan = p, Stamp = EF.Property<string>(p, ApiDbContext.ConcurrencyStamp) })
                .ToListAsync(cancellationToken);

            return TypedResults.Ok(rows.Select(r => r.Plan.ToDto(r.Stamp)).ToList());
        })
        .WithSummary("Every work plan with its operations.");

        group.MapGet("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var row = await db.WorkPlans
                .Include(p => p.Operations).ThenInclude(o => o.WorkCenter)
                .AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new { Plan = p, Stamp = EF.Property<string>(p, ApiDbContext.ConcurrencyStamp) })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null
                ? Problems.NotFound("work-plan")
                : Results.Ok(row.Plan.ToDto(row.Stamp));
        })
        .WithSummary("One work plan.");

        group.MapPost("/", async (WorkPlanDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var plan = new WorkPlan();
            plan.CopyHeaderFrom(request);
            plan.Operations = request.ToOperations();

            var centers = await db.WorkCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);
            var issues = WorkPlanValidator.Validate(plan, centers);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (await db.WorkPlans.AnyAsync(p => p.PlanNumber == plan.PlanNumber, cancellationToken))
                return Problems.Conflict(nameof(WorkPlanDto.PlanNumber), "Val_PlanNumberTaken");

            plan.CreatedUtc = plan.ModifiedUtc = DateTime.UtcNow;
            var entry = db.WorkPlans.Add(plan);
            ApiDbContext.Restamp(entry, expected: null);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/work-plans/{plan.Id}", plan.ToDto(ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Creates a work plan with its operations.");

        group.MapPut("/{id:int}", async (int id, WorkPlanDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConcurrencyStamp))
                return Problems.Validation(nameof(WorkPlanDto.ConcurrencyStamp), "Val_Required");

            var plan = await db.WorkPlans
                .Include(p => p.Operations)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (plan is null)
                return Problems.NotFound("work-plan");

            var previousStatus = plan.Status;
            var candidate = new WorkPlan { Id = id };
            candidate.CopyHeaderFrom(request);
            candidate.Operations = request.ToOperations();

            var centers = await db.WorkCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);
            var issues = WorkPlanValidator.Validate(candidate, centers, previousStatus);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (await db.WorkPlans.AnyAsync(p => p.PlanNumber == candidate.PlanNumber && p.Id != id, cancellationToken))
                return Problems.Conflict(nameof(WorkPlanDto.PlanNumber), "Val_PlanNumberTaken");

            plan.CopyHeaderFrom(request);
            plan.ModifiedUtc = DateTime.UtcNow;
            db.Operations.RemoveRange(plan.Operations);
            plan.Operations = candidate.Operations;

            var entry = db.Entry(plan);
            ApiDbContext.Restamp(entry, request.ConcurrencyStamp);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Problems.Concurrency("work-plan");
            }

            return Results.Ok(plan.ToDto(ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Replaces a work plan and its operations. Requires the concurrency stamp that was read.");

        group.MapDelete("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var plan = await db.WorkPlans.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (plan is null)
                return Problems.NotFound("work-plan");

            // Orders keep their own frozen routing, but the foreign key is
            // Restrict on purpose: deleting the plan an order came from would
            // erase where that order's snapshot originated.
            if (await db.ProductionOrders.AnyAsync(o => o.WorkPlanId == id, cancellationToken))
                return Problems.Conflict("WorkPlan", "Val_WorkPlanInUse");

            db.WorkPlans.Remove(plan);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Deletes a work plan no order came from.");
    }
}
