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
    public async Task<ScheduleResult> GenerateAsync(SchedulingParameters parameters)
    {
        var releasedPlans = (await _plans.GetAllAsync())
            .Where(p => p.Status == WorkPlanStatus.Released)
            .ToList();
        var centers = await _centers.GetAllAsync();

        var input = ScheduleMapper.BuildInput(releasedPlans, centers, parameters);
        if (input is null)
            return ScheduleResult.Empty(parameters.MinutesPerWorkingDay);

        var result = new SchedulingEngine().Run(input.Context);
        return ScheduleMapper.BuildView(result, input.Context, input.PlanById, parameters.MinutesPerWorkingDay);
    }
}
