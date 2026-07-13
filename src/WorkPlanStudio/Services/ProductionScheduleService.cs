using WorkPlanStudio.Models;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Loads the released work plans from the in-browser database, runs the
/// scheduling engine on them and projects the result for the UI. The mapping and
/// scoring live in pure, separately-tested classes (<see cref="ScheduleMapper"/>,
/// <see cref="SchedulingEngine"/>); this type is only the thin data-access shell.
/// </summary>
public sealed class ProductionScheduleService : IProductionScheduleService
{
    private readonly WorkPlanService _plans;
    private readonly WorkCenterService _centers;

    public ProductionScheduleService(WorkPlanService plans, WorkCenterService centers)
    {
        _plans = plans;
        _centers = centers;
    }

    /// <inheritdoc />
    public async Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        CancellationToken cancellationToken = default)
    {
        SchedulingParameterLimits.Validate(parameters);
        var releasedPlans = (await _plans.GetAllAsync(cancellationToken))
            .Where(p => p.Status == WorkPlanStatus.Released)
            .ToList();
        var centers = await _centers.GetAllAsync(cancellationToken);

        var preparation = ScheduleMapper.BuildInput(releasedPlans, centers, parameters);
        if (preparation.Input is null)
            return ScheduleResult.Empty(parameters.MinutesPerWorkingDay) with
            {
                PreparationErrors = preparation.Errors
            };

        var input = preparation.Input;
        var result = new SchedulingEngine().RunCancellable(input.Context, cancellationToken);
        var view = ScheduleMapper.BuildView(result, input.Context, input.PlanById, parameters.MinutesPerWorkingDay);
        return view with
        {
            Explanation = ScheduleExplainer.Explain(input.Context, result),
            PreparationErrors = preparation.Errors
        };
    }
}
