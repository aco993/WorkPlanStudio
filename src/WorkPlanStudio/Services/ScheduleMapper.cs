using WorkPlanStudio.Models;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services;

/// <summary>
/// Pure mapping between the app's stored entities and the scheduling engine — no
/// database and no Blazor — so the EF→domain boundary (the only place
/// <c>decimal</c> minutes are rounded to integer seconds) and the view projection
/// can be unit-tested directly, with hand-built entities.
/// </summary>
public static class ScheduleMapper
{
    /// <summary>Number of distinct job colours cycled through in the Gantt and table.</summary>
    public const int PaletteSize = 8;

    /// <summary>Whole-lot processing time of an operation, rounded to whole seconds (banker's rounding).</summary>
    public static long ToSeconds(decimal setupMinutes, decimal perPieceMinutes, int lotSize)
    {
        if (setupMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(setupMinutes));
        if (perPieceMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(perPieceMinutes));
        if (lotSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(lotSize));

        return checked((long)decimal.Round(
            checked((setupMinutes + checked(perPieceMinutes * lotSize)) * 60m),
            MidpointRounding.ToEven));
    }

    /// <summary>The mapped scheduling input plus a lookup back to what produced each job.</summary>
    /// <param name="Context">The engine input.</param>
    /// <param name="OriginById">Order reference and part name per job id.</param>
    /// <param name="Horizon">The wall-clock moment of second 0.</param>
    /// <param name="TimelineByWorkCenter">The annotated working-time timeline per work center, for the Gantt shading.</param>
    public sealed record Input(
        SchedulingContext Context,
        IReadOnlyDictionary<int, JobOrigin> OriginById,
        DateTime Horizon,
        IReadOnlyDictionary<int, WorkingTimeline> TimelineByWorkCenter);

    /// <summary>What a scheduled job came from, for labelling the Gantt and the table.</summary>
    public sealed record JobOrigin(string Reference, string PartName);

    /// <summary>
    /// Maps released production orders into a scheduling context, reading each
    /// order's frozen routing snapshot rather than the live work plan.
    /// <para>
    /// This is the difference that matters. Editing a work plan after an order is
    /// released changes nothing about that order's schedule, because the routing
    /// it will actually be built to was captured at release. Scheduling master
    /// data directly cannot make that promise, which is why this replaced the
    /// plan-based mapping rather than sitting beside it.
    /// </para>
    /// <para>
    /// The horizon is second 0 at the earliest release across the orders, so
    /// release and due dates become offsets on the engine's abstract time axis
    /// without dragging wall-clock or time zones into the core.
    /// </para>
    /// </summary>
    public static SchedulePreparationResult BuildInputFromOrders(
        IEnumerable<ProductionOrder> releasedOrders,
        IEnumerable<WorkCenter> centers,
        SchedulingParameters parameters,
        ShopCalendar? shopCalendar = null)
    {
        var orderList = releasedOrders as IReadOnlyList<ProductionOrder> ?? releasedOrders.ToList();
        var centerList = centers as IReadOnlyList<WorkCenter> ?? centers.ToList();
        var centerById = centerList.ToDictionary(center => center.Id);
        var calendar = shopCalendar ?? ShopCalendar.Unconstrained;

        var jobs = new List<ProductionJob>();
        var originById = new Dictionary<int, JobOrigin>();
        var errors = new List<SchedulePreparationIssue>();

        // Snapshots are decoded first: the horizon needs the earliest release
        // among the orders that actually made it through.
        var decoded = new List<(ProductionOrder Order, RoutingSnapshot Snapshot)>();
        foreach (var order in orderList)
        {
            var snapshot = RoutingSnapshot.Deserialize(order.RoutingSnapshotJson);
            if (snapshot is null || snapshot.Operations.Count == 0)
            {
                errors.Add(new(order.Id, order.OrderNumber, null, SchedulePreparationErrorCode.NoOperations, null));
                continue;
            }

            var orderErrors = ValidateSnapshot(order, snapshot, centerById);
            if (orderErrors.Count > 0)
            {
                errors.AddRange(orderErrors);
                continue;
            }

            decoded.Add((order, snapshot));
        }

        if (decoded.Count == 0)
            return new SchedulePreparationResult(null, errors);

        var horizon = decoded.Min(d => d.Order.ReleaseUtc);
        long horizonTicks = horizon.Ticks;

        // The working-time calendar of every active work center, built once per
        // run from the plant rules, the center's shift pattern and its absences.
        var timelines = new Dictionary<int, WorkingTimeline>();
        var machines = new List<MachineCapacity>();
        foreach (var center in centerList.Where(c => c.IsActive && c.ParallelCapacity is >= 1 and <= Validation.WorkCenterValidator.MaxCapacity))
        {
            var timeline = calendar.TimelineFor(center.Id, horizon);
            timelines[center.Id] = timeline;
            machines.Add(timeline.ToMachineCalendar(horizon)
                .ApplyTo(new MachineCapacity(center.Id, $"{center.Code} — {center.Name}", center.ParallelCapacity)));
        }

        var machineById = machines.ToDictionary(m => m.WorkCenterId);

        foreach (var (order, snapshot) in decoded)
        {
            var steps = snapshot.Operations
                .OrderBy(o => o.OperationNumber)
                .Select((o, i) => new JobStep(
                    i + 1,
                    o.WorkCenterId,
                    ToSeconds(o.SetupTimeMinutes, o.TimePerPieceMinutes, order.Quantity)))
                .ToList();

            // An operation longer than the longest shift (breaks bridged) can never
            // be placed. Reported here as a rejected order rather than thrown from
            // the engine's context constructor.
            var tooLong = snapshot.Operations
                .OrderBy(o => o.OperationNumber)
                .Select((o, i) => (Operation: o, Step: steps[i]))
                .Where(pair => pair.Step.DurationSeconds > machineById[pair.Step.WorkCenterId].LongestPlacementSeconds)
                .ToList();
            if (tooLong.Count > 0)
            {
                errors.AddRange(tooLong.Select(pair => new SchedulePreparationIssue(
                    order.Id, order.OrderNumber, pair.Operation.OperationNumber,
                    SchedulePreparationErrorCode.OperationExceedsShiftWindow,
                    centerById[pair.Step.WorkCenterId].Code)));
                continue;
            }

            jobs.Add(new ProductionJob
            {
                Id = order.Id,
                Reference = order.OrderNumber,
                ReleaseSeconds = ToOffsetSeconds(order.ReleaseUtc, horizonTicks),
                ExplicitDueSeconds = ToOffsetSeconds(order.DueUtc, horizonTicks),
                Weight = Math.Clamp(order.Priority, 1, 5),
                Steps = steps
            });
            originById[order.Id] = new JobOrigin(order.OrderNumber, snapshot.PartName);
        }

        var input = jobs.Count == 0
            ? null
            : new Input(new SchedulingContext(jobs, machines, parameters), originById, horizon, timelines);

        return new SchedulePreparationResult(input, errors);
    }

    private static long ToOffsetSeconds(DateTime moment, long horizonTicks) =>
        Math.Max(0, (moment.Ticks - horizonTicks) / TimeSpan.TicksPerSecond);

    private static List<SchedulePreparationIssue> ValidateSnapshot(
        ProductionOrder order,
        RoutingSnapshot snapshot,
        IReadOnlyDictionary<int, WorkCenter> centerById)
    {
        var errors = new List<SchedulePreparationIssue>();
        void Add(int? operationNumber, SchedulePreparationErrorCode code, string? center = null) =>
            errors.Add(new(order.Id, order.OrderNumber, operationNumber, code, center));

        if (order.Quantity <= 0)
            Add(null, SchedulePreparationErrorCode.InvalidLotSize);

        foreach (var operation in snapshot.Operations)
        {
            if (operation.OperationNumber <= 0)
                Add(operation.OperationNumber, SchedulePreparationErrorCode.InvalidOperationNumber);
            if (operation.SetupTimeMinutes < 0 || operation.TimePerPieceMinutes < 0)
                Add(operation.OperationNumber, SchedulePreparationErrorCode.InvalidOperationDuration);

            // The snapshot froze the routing, not the shop floor: a work center it
            // names can since have been deactivated or removed, and that is a real
            // problem the planner has to be told about.
            if (!centerById.TryGetValue(operation.WorkCenterId, out var center))
                Add(operation.OperationNumber, SchedulePreparationErrorCode.MissingWorkCenter, operation.WorkCenterId.ToString());
            else if (!center.IsActive)
                Add(operation.OperationNumber, SchedulePreparationErrorCode.InactiveWorkCenter, center.Code);
            else if (center.ParallelCapacity is < 1 or > Validation.WorkCenterValidator.MaxCapacity)
                Add(operation.OperationNumber, SchedulePreparationErrorCode.InvalidWorkCenterCapacity, center.Code);
        }

        return errors;
    }

    /// <summary>Projects an engine result into the page's Gantt rows, job table and KPI cards.</summary>
    public static ScheduleResult BuildView(
        SchedulingResult result,
        SchedulingContext context,
        IReadOnlyDictionary<int, JobOrigin> originById,
        int minutesPerWorkingDay) =>
        BuildView(result, context, originById, minutesPerWorkingDay, null, null);

    /// <summary>
    /// Projects an engine result into the page's Gantt rows, job table and KPI
    /// cards, shading each lane with the time its work center was closed.
    /// </summary>
    public static ScheduleResult BuildView(
        SchedulingResult result,
        SchedulingContext context,
        IReadOnlyDictionary<int, JobOrigin> originById,
        int minutesPerWorkingDay,
        DateTime? horizon,
        IReadOnlyDictionary<int, WorkingTimeline>? timelines)
    {
        // Stable colour per job (by plan number), shared between the Gantt and the table.
        var colorByJob = result.Schedule.Jobs
            .OrderBy(j => j.Reference)
            .Select((j, i) => (j.JobId, Color: i % PaletteSize))
            .ToDictionary(t => t.JobId, t => t.Color);

        var lateJobs = result.Schedule.Jobs.Where(j => j.IsLate).Select(j => j.JobId).ToHashSet();
        long makespan = result.Schedule.MakespanSeconds;

        var rows = new List<GanttRow>();
        foreach (var machine in context.Machines.Values)
        {
            var bars = result.Schedule.OnWorkCenter(machine.WorkCenterId)
                .Select(o => new GanttBar(
                    o.JobId, originById[o.JobId].Reference, colorByJob[o.JobId],
                    o.StepNumber, o.StartSeconds, o.EndSeconds, lateJobs.Contains(o.JobId))
                {
                    PausedSeconds = o.PausedSeconds,
                    SetupSeconds = o.SetupSeconds
                })
                .ToList();
            if (bars.Count > 0)
                rows.Add(new GanttRow(machine.Name, bars, ClosedSegments(machine.WorkCenterId, horizon, makespan, timelines)));
        }

        var jobRows = result.Schedule.Jobs
            .OrderBy(j => j.Reference)
            .Select(j => new JobRow(
                j.JobId, j.Reference, originById[j.JobId].PartName, colorByJob[j.JobId],
                j.DueSeconds, j.CompletionSeconds, j.LatenessSeconds, j.IsLate))
            .ToList();

        var e = result.Evaluation;
        var kpis = new ScheduleKpis(
            e.MakespanSeconds, e.OnTimeRate, e.TotalTardinessSeconds,
            e.AverageUtilization, e.LateJobCount, e.JobCount);

        return new ScheduleResult(true, kpis, rows, jobRows,
            result.Schedule.MakespanSeconds, minutesPerWorkingDay, result.LocalSearchSteps)
        {
            Horizon = horizon,
            TotalPausedSeconds = result.Schedule.Operations.Sum(o => o.PausedSeconds),
            UtilizationByWorkCenter = context.Machines.Values
                .Where(m => e.UtilizationByWorkCenter.ContainsKey(m.WorkCenterId))
                .ToDictionary(m => m.Name, m => e.UtilizationByWorkCenter[m.WorkCenterId])
        };
    }

    /// <summary>The closed stretches of one work center between the horizon and the makespan.</summary>
    private static IReadOnlyList<GanttClosedSegment> ClosedSegments(
        int workCenterId, DateTime? horizon, long makespan, IReadOnlyDictionary<int, WorkingTimeline>? timelines)
    {
        if (horizon is null || timelines is null || makespan <= 0 || !timelines.TryGetValue(workCenterId, out var timeline))
            return [];

        return timeline.Segments(horizon.Value, horizon.Value.AddSeconds(makespan))
            .Where(s => s.Kind != SegmentKind.Working)
            .Select(s => new GanttClosedSegment(
                (long)(s.Start - horizon.Value).TotalSeconds,
                (long)(s.End - horizon.Value).TotalSeconds,
                s.Kind,
                s.Label))
            .ToList();
    }
}
