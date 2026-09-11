using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Loads the released production orders from the in-browser database, runs the
/// scheduling engine on them and projects the result for the UI. The mapping and
/// scoring live in pure, separately-tested classes (<see cref="ScheduleMapper"/>,
/// <see cref="SchedulingEngine"/>); this type is only the thin data-access shell.
/// <para>
/// It does not call the engine itself. <see cref="IScheduleRunner"/> decides how
/// the search is executed — in slices on the UI thread here, on a worker wherever
/// one becomes available — so the choice of execution strategy is one registration
/// rather than an edit to this class or to the page. See ADR 0019.
/// </para>
/// </summary>
public sealed class ProductionScheduleService : IProductionScheduleService
{
    private readonly ProductionOrderService _orders;
    private readonly WorkCenterService _centers;
    private readonly PlantSettingsService _settings;
    private readonly IScheduleRunner _runner;

    public ProductionScheduleService(
        ProductionOrderService orders,
        WorkCenterService centers,
        PlantSettingsService settings,
        IScheduleRunner runner)
    {
        _orders = orders;
        _centers = centers;
        _settings = settings;
        _runner = runner;
    }

    /// <inheritdoc />
    public Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(parameters, IProductionScheduleService.DefaultMinutesPerWorkingDay, null, cancellationToken);

    /// <inheritdoc />
    public async Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        int minutesPerWorkingDay,
        IProgress<ScheduleRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        SchedulingParameterLimits.Validate(parameters);
        // Orders, not work plans. A work plan is master data that may be edited
        // at any time; an order carries the routing it was released with, so a
        // later edit cannot change work already on the shop floor.
        var orders = await _orders.GetSchedulableAsync(cancellationToken);
        var centers = await _centers.GetAllAsync(cancellationToken);
        var absences = await _centers.GetAbsencesAsync(cancellationToken);
        var settings = await _settings.GetAsync(cancellationToken);
        var calendar = ShopCalendar.From(settings, centers, absences);

        var preparation = ScheduleMapper.BuildInputFromOrders(orders, centers, parameters, calendar);
        if (preparation.Input is null)
            return ScheduleResult.Empty(minutesPerWorkingDay) with
            {
                PreparationErrors = preparation.Errors
            };

        var input = preparation.Input;
        var result = await _runner.RunAsync(input.Context, progress, cancellationToken);
        var view = ScheduleMapper.BuildView(
            result, input.Context, input.OriginById, minutesPerWorkingDay, input.Horizon, input.TimelineByWorkCenter);
        return view with
        {
            Explanation = ScheduleExplainer.Explain(input.Context, result),
            EquivalentRules = result.EquivalentRules,
            PreparationErrors = preparation.Errors
        };
    }
}
