using WorkPlanStudio.Models;

namespace WorkPlanStudio.Services;

/// <summary>
/// Builds a finite-capacity production schedule from released work plans.
/// The heuristic is deterministic: at each step it schedules the ready operation
/// with the earliest feasible start, then prefers shorter operations to reduce
/// average flow time.
/// </summary>
public sealed class WorkPlanSchedulingService
{
    private readonly WorkPlanService _plans;

    public WorkPlanSchedulingService(WorkPlanService plans) => _plans = plans;

    public async Task<SchedulingResult> CreateScheduleAsync(SchedulingOptions options)
    {
        var plans = await _plans.GetAllAsync();
        return BuildSchedule(plans, options);
    }

    public SchedulingResult BuildSchedule(IEnumerable<WorkPlan> plans, SchedulingOptions options)
    {
        ValidateOptions(options);

        var scheduleStart = MoveToWorkingTime(options.StartAt, options);
        var jobs = plans
            .Where(p => p.Status == WorkPlanStatus.Released || options.IncludeDrafts && p.Status == WorkPlanStatus.Draft)
            .Select(p => new JobState(p, scheduleStart))
            .Where(j => j.HasNext)
            .OrderBy(j => j.Plan.Status == WorkPlanStatus.Released ? 0 : 1)
            .ThenBy(j => j.Plan.ModifiedUtc)
            .ThenBy(j => j.Plan.PlanNumber)
            .ToList();

        if (jobs.Count == 0)
        {
            return new SchedulingResult
            {
                StartAt = scheduleStart,
                EndAt = scheduleStart,
                Warnings = ["No released work plans with operations are available for scheduling."]
            };
        }

        var workCenterAvailableAt = jobs
            .SelectMany(j => j.Operations)
            .Where(o => o.WorkCenterId > 0)
            .Select(o => o.WorkCenterId)
            .Distinct()
            .ToDictionary(id => id, _ => scheduleStart);

        var scheduled = new List<ScheduledOperation>();
        var warnings = new List<string>();

        while (jobs.Any(j => j.HasNext))
        {
            var candidates = jobs
                .Where(j => j.HasNext)
                .Select(j => CreateCandidate(j, workCenterAvailableAt, options, warnings))
                .OfType<Candidate>()
                .ToList();

            if (candidates.Count == 0)
                break;

            var candidate = candidates
                .OrderBy(c => c!.StartAt)
                .ThenBy(c => c!.DurationMinutes)
                .ThenBy(c => c!.Job.Plan.Status == WorkPlanStatus.Released ? 0 : 1)
                .ThenBy(c => c!.Job.Plan.PlanNumber)
                .First();
            var operation = candidate.Operation;
            var plan = candidate.Job.Plan;
            var workCenter = operation.WorkCenter;

            scheduled.Add(new ScheduledOperation
            {
                WorkPlanId = plan.Id,
                PlanNumber = plan.PlanNumber,
                PartNumber = plan.PartNumber,
                PartName = plan.PartName,
                OperationId = operation.Id,
                OperationNumber = operation.OperationNumber,
                Description = operation.Description,
                WorkCenterId = operation.WorkCenterId,
                WorkCenterCode = workCenter?.Code ?? "",
                WorkCenterName = workCenter?.Name ?? "",
                StartAt = candidate.StartAt,
                EndAt = candidate.EndAt,
                DurationMinutes = candidate.DurationMinutes,
                Cost = operation.Cost(plan.LotSize),
                QueueDelayMinutes = Math.Max(0, (candidate.StartAt - candidate.Job.ReadyAt).TotalMinutes)
            });

            candidate.Job.Advance(candidate.EndAt);
            workCenterAvailableAt[operation.WorkCenterId] = candidate.EndAt;
        }

        var endAt = scheduled.Count == 0 ? scheduleStart : scheduled.Max(i => i.EndAt);
        return new SchedulingResult
        {
            StartAt = scheduleStart,
            EndAt = endAt,
            Items = scheduled.OrderBy(i => i.StartAt).ThenBy(i => i.WorkCenterCode).ToList(),
            WorkCenters = CreateWorkCenterSummaries(scheduled, scheduleStart, endAt),
            Warnings = warnings.Distinct().ToList()
        };
    }

    private Candidate? CreateCandidate(
        JobState job,
        IReadOnlyDictionary<int, DateTime> workCenterAvailableAt,
        SchedulingOptions options,
        ICollection<string> warnings)
    {
        var operation = job.NextOperation;
        if (operation is null)
            return null;

        if (operation.WorkCenterId == 0 || operation.WorkCenter is null)
        {
            warnings.Add($"{job.Plan.PlanNumber} operation {operation.OperationNumber} has no valid work center.");
            job.SkipInvalidOperation();
            return null;
        }

        var resourceReadyAt = workCenterAvailableAt.TryGetValue(operation.WorkCenterId, out var value)
            ? value
            : MoveToWorkingTime(options.StartAt, options);
        var startAt = MoveToWorkingTime(Max(job.ReadyAt, resourceReadyAt), options);
        var duration = Math.Max(0m, operation.TotalTimeMinutes(job.Plan.LotSize));
        var endAt = AddWorkingMinutes(startAt, duration, options);

        return new Candidate(job, operation, startAt, endAt, duration);
    }

    private static IReadOnlyList<WorkCenterScheduleSummary> CreateWorkCenterSummaries(
        IReadOnlyCollection<ScheduledOperation> scheduled,
        DateTime scheduleStart,
        DateTime scheduleEnd)
    {
        var spanMinutes = Math.Max(1, (scheduleEnd - scheduleStart).TotalMinutes);

        return scheduled
            .GroupBy(i => new { i.WorkCenterId, i.WorkCenterCode, i.WorkCenterName })
            .Select(g => new WorkCenterScheduleSummary
            {
                WorkCenterId = g.Key.WorkCenterId,
                WorkCenterCode = g.Key.WorkCenterCode,
                WorkCenterName = g.Key.WorkCenterName,
                BookedMinutes = g.Sum(i => i.DurationMinutes),
                FirstStartAt = g.Min(i => i.StartAt),
                LastEndAt = g.Max(i => i.EndAt),
                UtilizationPercent = Math.Min(100, (double)g.Sum(i => i.DurationMinutes) / spanMinutes * 100)
            })
            .OrderByDescending(w => w.BookedMinutes)
            .ThenBy(w => w.WorkCenterCode)
            .ToList();
    }

    private static DateTime AddWorkingMinutes(DateTime startAt, decimal minutes, SchedulingOptions options)
    {
        var remaining = (double)minutes;
        var cursor = MoveToWorkingTime(startAt, options);

        while (remaining > 0.0001)
        {
            var dayEnd = cursor.Date.Add(options.WorkDayEnd);
            var availableToday = Math.Max(0, (dayEnd - cursor).TotalMinutes);

            if (remaining <= availableToday)
                return cursor.AddMinutes(remaining);

            remaining -= availableToday;
            cursor = MoveToWorkingTime(cursor.Date.AddDays(1).Add(options.WorkDayStart), options);
        }

        return cursor;
    }

    private static DateTime MoveToWorkingTime(DateTime value, SchedulingOptions options)
    {
        var cursor = value;

        while (options.ExcludeWeekends && IsWeekend(cursor))
            cursor = cursor.Date.AddDays(1).Add(options.WorkDayStart);

        var dayStart = cursor.Date.Add(options.WorkDayStart);
        var dayEnd = cursor.Date.Add(options.WorkDayEnd);

        if (cursor < dayStart)
            return dayStart;

        if (cursor >= dayEnd)
            return MoveToWorkingTime(cursor.Date.AddDays(1).Add(options.WorkDayStart), options);

        return cursor;
    }

    private static bool IsWeekend(DateTime value) =>
        value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;

    private static void ValidateOptions(SchedulingOptions options)
    {
        if (options.WorkDayEnd <= options.WorkDayStart)
            throw new ArgumentException("Work day end must be after work day start.", nameof(options));
    }

    private sealed class JobState
    {
        private int _nextIndex;

        public JobState(WorkPlan plan, DateTime readyAt)
        {
            Plan = plan;
            ReadyAt = readyAt;
            Operations = plan.Operations.OrderBy(o => o.OperationNumber).ToList();
        }

        public WorkPlan Plan { get; }
        public DateTime ReadyAt { get; private set; }
        public IReadOnlyList<Operation> Operations { get; }
        public bool HasNext => _nextIndex < Operations.Count;
        public Operation? NextOperation => HasNext ? Operations[_nextIndex] : null;

        public void Advance(DateTime readyAt)
        {
            ReadyAt = readyAt;
            _nextIndex++;
        }

        public void SkipInvalidOperation() => _nextIndex++;
    }

    private readonly record struct Candidate(
        JobState Job,
        Operation Operation,
        DateTime StartAt,
        DateTime EndAt,
        decimal DurationMinutes);
}
