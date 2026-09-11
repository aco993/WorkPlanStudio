using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

/// <summary>The accounting units work centers book their machine hours against.</summary>
public static class CostCenterEndpoints
{
    /// <summary>
    /// Maps the cost-centre routes.
    /// <para>
    /// The same split as the other master data: reading needs an account and no
    /// particular role, writing needs <see cref="PolicyNames.ManageMasterData"/>.
    /// A cost centre is Controlling's data — a supervisor books against it, a
    /// planner owns it — so giving it a policy of its own would have added a
    /// fourth answer to a question the repository already answers once.
    /// </para>
    /// <para>
    /// Delete refuses a cost centre work centres still point at. The pre-check is
    /// what produces the sentence; the <c>Restrict</c> foreign key behind it is
    /// what makes the rule true even if a future endpoint forgets to ask.
    /// </para>
    /// </summary>
    /// <param name="app">The route builder to add to.</param>
    public static void MapCostCenterEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/cost-centers")
            .RequireAuthorization()
            .WithTags("Cost centers");

        group.MapGet("/", async (ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var rows = await db.CostCenters
                .AsNoTracking()
                .OrderBy(c => c.Code)
                .Select(c => new { CostCenter = c, Stamp = EF.Property<string>(c, ApiDbContext.ConcurrencyStamp) })
                .ToListAsync(cancellationToken);

            return TypedResults.Ok(rows.Select(r => r.CostCenter.ToDto(r.Stamp)).ToList());
        })
        .WithSummary("Every cost center, by code.");

        group.MapGet("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var row = await db.CostCenters
                .AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => new { CostCenter = c, Stamp = EF.Property<string>(c, ApiDbContext.ConcurrencyStamp) })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null
                ? Problems.NotFound("cost-center")
                : Results.Ok(row.CostCenter.ToDto(row.Stamp));
        })
        .WithSummary("One cost center.");

        group.MapPost("/", async (CostCenterDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var costCenter = request.ToEntity();

            var issues = CostCenterValidator.Validate(costCenter);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (await db.CostCenters.AnyAsync(c => c.Code == costCenter.Code, cancellationToken))
                return Problems.Conflict(nameof(CostCenterDto.Code), "Val_CostCenterCodeTaken");

            var entry = db.CostCenters.Add(costCenter);
            ApiDbContext.Restamp(entry, expected: null);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/cost-centers/{costCenter.Id}", costCenter.ToDto(ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Creates a cost center.");

        group.MapPut("/{id:int}", async (int id, CostCenterDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConcurrencyStamp))
                return Problems.Validation(nameof(CostCenterDto.ConcurrencyStamp), "Val_Required");

            var costCenter = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (costCenter is null)
                return Problems.NotFound("cost-center");

            costCenter.CopyFrom(request);

            var issues = CostCenterValidator.Validate(costCenter);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            if (await db.CostCenters.AnyAsync(c => c.Code == costCenter.Code && c.Id != id, cancellationToken))
                return Problems.Conflict(nameof(CostCenterDto.Code), "Val_CostCenterCodeTaken");

            var entry = db.Entry(costCenter);
            ApiDbContext.Restamp(entry, request.ConcurrencyStamp);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Problems.Concurrency("cost-center");
            }

            return Results.Ok(costCenter.ToDto(ApiDbContext.StampOf(entry)));
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Replaces a cost center. Requires the concurrency stamp that was read.");

        group.MapDelete("/{id:int}", async (int id, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var costCenter = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (costCenter is null)
                return Problems.NotFound("cost-center");

            if (await db.WorkCenters.AnyAsync(w => w.CostCenterId == id, cancellationToken))
                return Problems.Conflict("CostCenter", "Val_CostCenterInUse");

            db.CostCenters.Remove(costCenter);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .WithSummary("Deletes a cost center no work center books against.");
    }
}
