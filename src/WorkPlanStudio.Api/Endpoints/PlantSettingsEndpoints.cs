using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

/// <summary>The plant-wide working-time settings — one row, two verbs.</summary>
public static class PlantSettingsEndpoints
{
    /// <summary>
    /// Maps the plant-settings routes. Reading is open to any account because the
    /// rules explain every closed stretch on a Gantt chart; writing them changes
    /// what the whole plant may legally do, which is why it is its own policy and
    /// not part of master data.
    /// </summary>
    /// <param name="app">The route builder to add to.</param>
    public static void MapPlantSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/plant-settings")
            .RequireAuthorization()
            .WithTags("Plant settings");

        group.MapGet("/", async (ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var settings = await db.PlantSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == PlantSettings.SingletonId, cancellationToken)
                ?? new PlantSettings();

            return TypedResults.Ok(settings.ToDto());
        })
        .WithSummary("The plant's working-time settings.");

        group.MapPut("/", async (PlantSettingsDto request, ApiDbContext db, CancellationToken cancellationToken) =>
        {
            var candidate = new PlantSettings();
            candidate.CopyFrom(request);

            // The application's own validator, not a copy of it: the server and
            // the browser refuse the same row for the same reason.
            var issues = PlantSettingsValidator.Validate(candidate);
            if (issues.Count > 0)
                return Problems.Validation(issues);

            var settings = await db.PlantSettings
                .FirstOrDefaultAsync(s => s.Id == PlantSettings.SingletonId, cancellationToken);

            if (settings is null)
            {
                settings = new PlantSettings();
                db.PlantSettings.Add(settings);
            }

            settings.CopyFrom(request);
            settings.ModifiedUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(settings.ToDto());
        })
        .RequireAuthorization(PolicyNames.ManagePlantRules)
        .WithSummary("Replaces the plant's working-time settings.");
    }
}
