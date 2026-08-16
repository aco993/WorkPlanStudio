using WorkPlanStudio.Models;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Loads the released production orders from the in-browser database, runs the
/// scheduling engine on them and projects the result for the UI. The mapping and
/// scoring live in pure, separately-tested classes (<see cref="ScheduleMapper"/>,
/// <see cref="SchedulingEngine"/>); this type is only the thin data-access shell.
/// </summary>
public sealed class ProductionScheduleService : IProductionScheduleService
{
    private readonly ProductionOrderService _orders;
    private readonly WorkCenterService _centers;

    public ProductionScheduleService(ProductionOrderService orders, WorkCenterService centers)
    {
        _orders = orders;
        _centers = centers;
    }

    /// <inheritdoc />
    public async Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        CancellationToken cancellationToken = default)
    {
        SchedulingParameterLimits.Validate(parameters);
        // Orders, not work plans. A work plan is master data that may be edited
        // at any time; an order carries the routing it was released with, so a
        // later edit cannot change work already on the shop floor.
        var orders = await _orders.GetSchedulableAsync(cancellationToken);
        var centers = await _centers.GetAllAsync(cancellationToken);

        var preparation = ScheduleMapper.BuildInputFromOrders(orders, centers, parameters);
        if (preparation.Input is null)
            return ScheduleResult.Empty(parameters.MinutesPerWorkingDay) with
            {
                PreparationErrors = preparation.Errors
            };

        var input = preparation.Input;
        var result = new SchedulingEngine().RunCancellable(input.Context, cancellationToken);
        var view = ScheduleMapper.BuildView(result, input.Context, input.OriginById, parameters.MinutesPerWorkingDay);
        return view with
        {
            Explanation = ScheduleExplainer.Explain(input.Context, result),
            EquivalentRules = result.EquivalentRules,
            PreparationErrors = preparation.Errors
        };
    }
}
