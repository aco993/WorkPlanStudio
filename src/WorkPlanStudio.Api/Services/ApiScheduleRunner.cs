using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services;

namespace WorkPlanStudio.Api.Services;

/// <summary>
/// Runs a schedule on the server.
/// <para>
/// Every step below is the browser's <c>ProductionScheduleService</c>, line for
/// line, over a different store: released orders in, <see cref="ShopCalendar"/>
/// built from the plant rules, <see cref="ScheduleMapper"/> to the engine input,
/// one <see cref="SchedulingEngine"/> run, the same projection back. That is the
/// whole point of the endpoint — not a second scheduler that happens to agree,
/// but the same one, reached from a second host.
/// </para>
/// <para>
/// The engine is deterministic by construction (integer seconds, a seeded PRNG,
/// no wall-clock inside the core), which is what makes "the same input yields
/// the same schedule on a server and in a browser" a property that can be
/// asserted rather than hoped for.
/// </para>
/// </summary>
public sealed class ApiScheduleRunner
{
    private readonly ApiDbContext _db;

    /// <summary>Creates the runner.</summary>
    /// <param name="db">The store to read orders, work centers, absences and plant settings from.</param>
    public ApiScheduleRunner(ApiDbContext db) => _db = db;

    /// <summary>Loads the tenant's data, schedules it and projects the result for the wire.</summary>
    /// <param name="parameters">Engine parameters; already range-checked by the endpoint.</param>
    /// <param name="minutesPerWorkingDay">Working minutes per calendar day, used only to render the chart.</param>
    /// <param name="cancellationToken">Cancels the reads and the search.</param>
    public async Task<ScheduleRunResponse> RunAsync(
        SchedulingParameters parameters,
        int minutesPerWorkingDay = ScheduleResult.DefaultMinutesPerWorkingDay,
        CancellationToken cancellationToken = default)
    {
        SchedulingParameterLimits.Validate(parameters);

        var orders = await _db.ProductionOrders
            .AsNoTracking()
            .Where(o => o.Status == ProductionOrderStatus.Released && o.RoutingSnapshotJson != "")
            .OrderBy(o => o.DueLocal)
            .ToListAsync(cancellationToken);

        var centers = await _db.WorkCenters
            .AsNoTracking()
            .OrderBy(c => c.Code)
            .ToListAsync(cancellationToken);

        var absences = await _db.WorkCenterAbsences
            .AsNoTracking()
            .OrderBy(a => a.Start)
            .ThenBy(a => a.WorkCenterId)
            .ToListAsync(cancellationToken);

        var settings = await _db.PlantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == Models.PlantSettings.SingletonId, cancellationToken)
            ?? new PlantSettings();

        var calendar = ShopCalendar.From(settings, centers, absences);
        var preparation = ScheduleMapper.BuildInputFromOrders(orders, centers, parameters, calendar);

        if (preparation.Input is null)
        {
            var empty = ScheduleResult.Empty(minutesPerWorkingDay) with
            {
                PreparationErrors = preparation.Errors
            };
            return ScheduleMapping.ToResponse(empty, signature: "");
        }

        var input = preparation.Input;
        var result = new SchedulingEngine().RunCancellable(input.Context, cancellationToken);
        var view = ScheduleMapper.BuildView(
            result,
            input.Context,
            input.OriginById,
            minutesPerWorkingDay,
            input.Horizon,
            input.TimelineByWorkCenter) with
        {
            Explanation = ScheduleExplainer.Explain(input.Context, result),
            EquivalentRules = result.EquivalentRules,
            PreparationErrors = preparation.Errors
        };

        return ScheduleMapping.ToResponse(view, result.Schedule.Signature());
    }
}
