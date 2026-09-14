using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Api.Services;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Services;

namespace WorkPlanStudio.Api.Endpoints;

/// <summary>Running the scheduling engine server-side.</summary>
public static class ScheduleEndpoints
{
    /// <summary>
    /// Maps <c>POST /api/schedule/run</c>.
    /// <para>
    /// Open to any signed-in account, guests included: looking at what the plant
    /// would do is not a change to the plant, and the browser's own permission
    /// table says the same — no policy guards running a schedule there either.
    /// </para>
    /// </summary>
    /// <param name="app">The route builder to add to.</param>
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/schedule/run", async (
            ScheduleRunRequest? request,
            ApiScheduleRunner runner,
            CancellationToken cancellationToken) =>
        {
            request ??= new ScheduleRunRequest();

            // The display day is no longer an engine parameter, so the engine's
            // limits no longer police it. The check moved here with the value.
            var minutesPerWorkingDay = ScheduleMapping.MinutesPerWorkingDay(request);
            if (!ScheduleResult.IsDrawableDay(minutesPerWorkingDay))
                return Problems.Validation(nameof(request.MinutesPerWorkingDay), "Val_Range");

            var parameters = ScheduleMapping.ToParameters(request);

            try
            {
                return Results.Ok(await runner.RunAsync(parameters, minutesPerWorkingDay, cancellationToken));
            }
            catch (ArgumentOutOfRangeException exception)
            {
                // The engine's own limits are the authority on what a parameter may
                // be; re-stating them here would be a second, drifting copy.
                return Problems.Validation(exception.ParamName ?? "parameters", "Val_Range");
            }
        })
        .RequireAuthorization()
        .WithTags("Schedule")
        .WithSummary("Runs the scheduling engine over the released orders and returns the schedule.");
    }
}
