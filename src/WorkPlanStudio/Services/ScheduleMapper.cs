using WorkPlanStudio.Models;
using WorkPlanStudio.Scheduling;

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
    public static long ToSeconds(decimal setupMinutes, decimal perPieceMinutes, int lotSize) =>
        (long)decimal.Round((setupMinutes + perPieceMinutes * lotSize) * 60m, MidpointRounding.ToEven);

    /// <summary>The mapped scheduling input plus a lookup back to the originating plans.</summary>
    public sealed record Input(SchedulingContext Context, IReadOnlyDictionary<int, WorkPlan> PlanById);

    /// <summary>
    /// Maps released plans + active work centers into a scheduling context, or
    /// returns <c>null</c> when nothing is schedulable. Operations on inactive or
    /// unknown work centers are dropped; plans left without steps are skipped;
    /// step numbers are re-indexed so malformed data cannot break the engine.
    /// </summary>
    public static Input? BuildInput(
        IEnumerable<WorkPlan> releasedPlans,
        IEnumerable<WorkCenter> centers,
        SchedulingParameters parameters)
    {
        var centerList = centers as IReadOnlyList<WorkCenter> ?? centers.ToList();
        var activeIds = centerList.Where(c => c.IsActive).Select(c => c.Id).ToHashSet();

        var machines = centerList
            .Where(c => c.IsActive)
            .Select(c => new MachineCapacity(c.Id, $"{c.Code} — {c.Name}", ParallelCapacity: 1))
            .ToList();

        var jobs = new List<ProductionJob>();
        var planById = new Dictionary<int, WorkPlan>();
        foreach (var plan in releasedPlans)
        {
            var ordered = plan.Operations
                .Where(o => activeIds.Contains(o.WorkCenterId))
                .OrderBy(o => o.OperationNumber)
                .ToList();
            if (ordered.Count == 0)
                continue;

            var steps = ordered
                .Select((o, i) => new JobStep(i + 1, o.WorkCenterId, ToSeconds(o.SetupTimeMinutes, o.TimePerPieceMinutes, plan.LotSize)))
                .ToList();

            jobs.Add(new ProductionJob
            {
                Id = plan.Id,
                Reference = plan.PlanNumber,
                ReleaseSeconds = 0,
                Weight = Math.Max(1, plan.LotSize),
                Steps = steps
            });
            planById[plan.Id] = plan;
        }

        if (jobs.Count == 0)
            return null;

        return new Input(new SchedulingContext(jobs, machines, parameters), planById);
    }

    /// <summary>Projects an engine result into the page's Gantt rows, job table and KPI cards.</summary>
    public static ScheduleResult BuildView(
        SchedulingResult result,
        SchedulingContext context,
        IReadOnlyDictionary<int, WorkPlan> planById,
        int minutesPerWorkingDay)
    {
        // Stable colour per job (by plan number), shared between the Gantt and the table.
        var colorByJob = result.Schedule.Jobs
            .OrderBy(j => j.Reference)
            .Select((j, i) => (j.JobId, Color: i % PaletteSize))
            .ToDictionary(t => t.JobId, t => t.Color);

        var lateJobs = result.Schedule.Jobs.Where(j => j.IsLate).Select(j => j.JobId).ToHashSet();

        var rows = new List<GanttRow>();
        foreach (var machine in context.Machines.Values)
        {
            var bars = result.Schedule.OnWorkCenter(machine.WorkCenterId)
                .Select(o => new GanttBar(
                    o.JobId, planById[o.JobId].PlanNumber, colorByJob[o.JobId],
                    o.StepNumber, o.StartSeconds, o.EndSeconds, lateJobs.Contains(o.JobId)))
                .ToList();
            if (bars.Count > 0)
                rows.Add(new GanttRow(machine.Name, bars));
        }

        var jobRows = result.Schedule.Jobs
            .OrderBy(j => j.Reference)
            .Select(j => new JobRow(
                j.JobId, j.Reference, planById[j.JobId].PartName, colorByJob[j.JobId],
                j.DueSeconds, j.CompletionSeconds, j.LatenessSeconds, j.IsLate))
            .ToList();

        var e = result.Evaluation;
        var kpis = new ScheduleKpis(
            e.MakespanSeconds, e.OnTimeRate, e.TotalTardinessSeconds,
            e.AverageUtilization, e.LateJobCount, e.JobCount);

        return new ScheduleResult(true, kpis, rows, jobRows,
            result.Schedule.MakespanSeconds, minutesPerWorkingDay, result.LocalSearchSteps);
    }
}
