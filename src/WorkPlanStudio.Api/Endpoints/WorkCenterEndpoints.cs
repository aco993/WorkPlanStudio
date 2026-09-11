using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Api.Endpoints;

/// <summary>Work centers and their one-off closed periods.</summary>
public static class WorkCenterEndpoints
{
    /// <summary>
    /// Maps the work-center routes.
    /// <para>
    /// Reading needs an account but no particular role; every write needs
    /// <see cref="PolicyNames.ManageMasterData"/>, except the absence routes,
    /// which a supervisor may use too — the same split the browser's persona
    /// table makes, enforced by the same policy names.
    /// </para>
    /// </summary>
    /// <param name="app">The route builder to add to.</param>
    public static void MapWorkCenterEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/work-centers")
            .RequireAuthorization()
            .WithTags("Work centers");

        group.MapGet("/", async (ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var rows = await db.WorkCenters
                .AsNoTracking()
                .Include(c => c.CostCenter)
                .OrderBy(c => c.Code)
                .Select(c => new { Center = c, Stamp = EF.Property<string>(c, ApiDbContext.ConcurrencyStamp) })
                .ToListAsync(cancellationToken);

            return TypedResults.Ok(rows.Select(r => r.Center.ToDto(r.Stamp)).ToList());
        })
        .WithSummary("Every work center, by code.");

        group.MapGet("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var row = await db.WorkCenters
                .AsNoTracking()
                .Include(c => c.CostCenter)
                .Where(c => c.Id == id)
                .Select(c => new { Center = c, Stamp = EF.Property<string>(c, ApiDbContext.ConcurrencyStamp) })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null
                ? Problems.NotFound("work-center")
                : Results.Ok(row.Center.ToDto(row.Stamp));
        })
        .WithSummary("One work center.");

        group.MapPost("/", async (WorkCenterDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var center = request.ToEntity();
            var issues = WorkCenterValidator.Validate(center);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (await db.WorkCenters.AnyAsync(c => c.Code == center.Code, cancellationToken))
                return Problems.Conflict(nameof(WorkCenterDto.Code), "Val_CodeTaken");

            var costCenter = await ResolveCostCenterAsync(db, center.CostCenterId, cancellationToken);
            if (center.CostCenterId is not null && costCenter is null)
                return Problems.Validation(nameof(WorkCenterDto.CostCenterId), "Val_CostCenterMissing");

            var entry = db.WorkCenters.Add(center);
            ApiDbContext.Restamp(entry, expected: null);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created(
                $"/api/work-centers/{center.Id}",
                center.ToDto(ApiDbContext.StampOf(entry), costCenter));
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Creates a work center.");

        group.MapPut("/{id:int}", async (int id, WorkCenterDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConcurrencyStamp))
                return Problems.Validation(nameof(WorkCenterDto.ConcurrencyStamp), "Val_Required");

            var center = await db.WorkCenters.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (center is null)
                return Problems.NotFound("work-center");

            var wasActive = center.IsActive;
            center.CopyFrom(request);

            var issues = WorkCenterValidator.Validate(center);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (await db.WorkCenters.AnyAsync(c => c.Code == center.Code && c.Id != id, cancellationToken))
                return Problems.Conflict(nameof(WorkCenterDto.Code), "Val_CodeTaken");

            var costCenter = await ResolveCostCenterAsync(db, center.CostCenterId, cancellationToken);
            if (center.CostCenterId is not null && costCenter is null)
                return Problems.Validation(nameof(WorkCenterDto.CostCenterId), "Val_CostCenterMissing");

            // Deactivating a center that live work still points at would make it
            // unschedulable without anybody being told. The question that matters
            // is whether a released *order* still needs it: asking only about
            // released plans is routable around by setting the plan back to Draft
            // first. The plan check stays too — a released routing is also a
            // commitment.
            if (wasActive && !center.IsActive)
            {
                if (await db.OrderRoutingCenters.AnyAsync(
                        r => r.WorkCenterId == id && r.ProductionOrder!.Status == ProductionOrderStatus.Released,
                        cancellationToken))
                    return Problems.Conflict(nameof(WorkCenterDto.IsActive), "Val_WorkCenterOrderUse");

                if (await db.Operations.AnyAsync(
                        o => o.WorkCenterId == id && o.WorkPlan!.Status == WorkPlanStatus.Released, cancellationToken))
                    return Problems.Conflict(nameof(WorkCenterDto.IsActive), "Val_WorkCenterReleasedUse");
            }

            var entry = db.Entry(center);
            ApiDbContext.Restamp(entry, request.ConcurrencyStamp);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Problems.Concurrency("work-center");
            }

            return Results.Ok(center.ToDto(ApiDbContext.StampOf(entry), costCenter));
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Replaces a work center. Requires the concurrency stamp that was read.");

        group.MapDelete("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var center = await db.WorkCenters.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (center is null)
                return Problems.NotFound("work-center");

            if (await db.Operations.AnyAsync(o => o.WorkCenterId == id, cancellationToken))
                return Problems.Conflict("WorkCenter", "Val_WorkCenterInUse");

            // A released order's frozen routing names work centers inside a JSON
            // blob SQL cannot see. These rows are that blob's index, which is why
            // the delete can be refused rather than discovered later as a
            // MissingWorkCenter on the schedule page.
            if (await db.OrderRoutingCenters.AnyAsync(r => r.WorkCenterId == id, cancellationToken))
                return Problems.Conflict("WorkCenter", "Val_WorkCenterOrderUse");

            db.WorkCenters.Remove(center);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Deletes a work center no operation and no released order references.");

        group.MapGet("/absences", async (ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var absences = await db.WorkCenterAbsences
                .AsNoTracking()
                .OrderBy(a => a.Start)
                .ThenBy(a => a.WorkCenterId)
                .ToListAsync(cancellationToken);

            return TypedResults.Ok(absences.Select(ApiMapping.ToDto).ToList());
        })
        .WithSummary("Every recorded absence, oldest first.");

        group.MapPost("/absences", async (WorkCenterAbsenceDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var absence = new WorkCenterAbsence
            {
                WorkCenterId = request.WorkCenterId,

                // Plant wall clock, like every other planning date. A caller that
                // serialised a "Z" is describing its own zone, which this model
                // does not have; a row whose Start is UTC and whose End is not
                // reports a duration its own JSON contradicts.
                Start = PlantTime.Wall(request.Start),
                End = PlantTime.Wall(request.End),
                Kind = (AbsenceKind)request.Kind,
                Label = request.Label?.Trim() ?? ""
            };

            var issues = WorkCenterValidator.ValidateAbsence(absence);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (!await db.WorkCenters.AnyAsync(c => c.Id == absence.WorkCenterId, cancellationToken))
                return Problems.NotFound("work-center");

            db.WorkCenterAbsences.Add(absence);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/work-centers/absences/{absence.Id}", absence.ToDto());
        })
        .RequireAuthorization(PolicyNames.ManageAbsences)
        .WithSummary("Records a closed period for one work center.");

        group.MapDelete("/absences/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var absence = await db.WorkCenterAbsences.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (absence is null)
                return Problems.NotFound("absence");

            db.WorkCenterAbsences.Remove(absence);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization(PolicyNames.ManageAbsences)
        .WithSummary("Removes a recorded absence.");
    }

    /// <summary>
    /// The cost centre a request names, or <c>null</c> when it names none or names
    /// one that does not exist. The caller decides which of those two it is; the
    /// point of resolving here is that the row is fetched rather than trusted, so
    /// the response's display fields describe the cost centre the server has and
    /// not the one the caller claimed.
    /// </summary>
    private static async Task<CostCenter?> ResolveCostCenterAsync(
        ApiDbContext db,
        int? costCenterId,
        CancellationToken cancellationToken) =>
        costCenterId is { } id
            ? await db.CostCenters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            : null;
}
