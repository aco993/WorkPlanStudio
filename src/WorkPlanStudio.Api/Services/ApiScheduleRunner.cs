using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Mapping;
using WorkPlanStudio.Api.Telemetry;
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
    private readonly ApiTelemetry _telemetry;
    private readonly TimeProvider _time;

    /// <summary>Creates the runner.</summary>
    /// <param name="db">The store to read orders, work centers, absences and plant settings from.</param>
    /// <param name="telemetry">Where the run's duration and size are recorded.</param>
    /// <param name="time">The clock the duration is measured with.</param>
    public ApiScheduleRunner(ApiDbContext db, ApiTelemetry telemetry, TimeProvider time)
    {
        _db = db;
        _telemetry = telemetry;
        _time = time;
    }

    /// <summary>Loads the tenant's data, schedules it and projects the result for the wire.</summary>
    /// <param name="parameters">Engine parameters; already range-checked by the endpoint.</param>
    /// <param name="minutesPerWorkingDay">Working minutes per calendar day, used only to render the chart.</param>
    /// <param name="cancellationToken">Cancels the reads and the search.</param>
    public async Task<ScheduleRunResponse> RunAsync(
        SchedulingParameters parameters,
        int minutesPerWorkingDay = ScheduleResult.DefaultMinutesPerWorkingDay,
        CancellationToken cancellationToken = default)
    {
        // One span for the run, with the reads and the search as children: the
        // question "was it the database or the solver?" is answered by looking,
        // not by adding a stopwatch after the fact. The outcome is a tag on both
        // the span and the duration histogram, so a slow preparation failure is
        // not averaged in with real solves.
        using var run = ApiTelemetry.Source.StartActivity("schedule.run");
        run?.SetTag("workplan.dispatch_rule", parameters.DispatchRule.ToString());
        var started = _time.GetTimestamp();
        var outcome = "failed";
        try
        {
            SchedulingParameterLimits.Validate(parameters);

            List<ProductionOrder> orders;
            List<WorkCenter> centers;
            List<WorkCenterAbsence> absences;
            PlantSettings settings;
            using (var load = ApiTelemetry.Source.StartActivity("schedule.load"))
            {
                orders = await _db.ProductionOrders
                    .AsNoTracking()
                    .Where(o => o.Status == ProductionOrderStatus.Released && o.RoutingSnapshotJson != "")
                    .OrderBy(o => o.DueLocal)
                    .ToListAsync(cancellationToken);

                centers = await _db.WorkCenters
                    .AsNoTracking()
                    .OrderBy(c => c.Code)
                    .ToListAsync(cancellationToken);

                absences = await _db.WorkCenterAbsences
                    .AsNoTracking()
                    .OrderBy(a => a.Start)
                    .ThenBy(a => a.WorkCenterId)
                    .ToListAsync(cancellationToken);

                settings = await _db.PlantSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == Models.PlantSettings.SingletonId, cancellationToken)
                    ?? new PlantSettings();

                load?.SetTag("workplan.orders", orders.Count);
                load?.SetTag("workplan.work_centers", centers.Count);
                load?.SetTag("workplan.absences", absences.Count);
            }

            var calendar = ShopCalendar.From(settings, centers, absences);
            var preparation = ScheduleMapper.BuildInputFromOrders(orders, centers, parameters, calendar);

            if (preparation.Input is null)
            {
                var empty = ScheduleResult.Empty(minutesPerWorkingDay) with
                {
                    PreparationErrors = preparation.Errors
                };
                outcome = "not_schedulable";
                run?.SetTag("workplan.preparation_errors", preparation.Errors.Count);
                return ScheduleMapping.ToResponse(empty, signature: "");
            }

            var input = preparation.Input;
            SchedulingResult result;
            using (ApiTelemetry.Source.StartActivity("schedule.solve"))
                result = new SchedulingEngine().RunCancellable(input.Context, cancellationToken);
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

            outcome = "scheduled";
            run?.SetTag("workplan.jobs", result.Schedule.Jobs.Count);
            run?.SetTag("workplan.operations", result.Schedule.Operations.Count);
            _telemetry.ScheduledOperations.Add(result.Schedule.Operations.Count);
            return ScheduleMapping.ToResponse(view, result.Schedule.Signature());
        }
        catch (ArgumentOutOfRangeException)
        {
            outcome = "invalid_parameters";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            run?.SetTag("workplan.outcome", outcome);
            if (outcome == "failed")
                run?.SetStatus(ActivityStatusCode.Error);
            _telemetry.ScheduleRunDuration.Record(
                _time.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
