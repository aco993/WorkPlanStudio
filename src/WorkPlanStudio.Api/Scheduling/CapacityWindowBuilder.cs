using WorkPlanStudio.Models;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Api.Scheduling;

internal static class CapacityWindowBuilder
{
    public static MachineCapacity Build(WorkCenter center, DateTime horizonStartUtc, DateTime horizonEndUtc)
    {
        var shifts = center.CalendarShifts.OrderBy(shift => shift.DayOfWeek).ThenBy(shift => shift.StartMinute).ToList();
        var windows = shifts.Count == 0
            ? new List<(DateTime Start, DateTime End)>()
            : BuildShiftWindows(center.TimeZoneId, shifts, horizonStartUtc, horizonEndUtc);
        if (windows.Count > 0)
            windows = SubtractDowntimes(windows, center.Downtimes);

        return new MachineCapacity(center.Id, $"{center.Code} — {center.Name}", center.ParallelCapacity)
        {
            AvailabilityWindows = windows.Select(window => new CapacityWindow(
                Seconds(horizonStartUtc, window.Start), Seconds(horizonStartUtc, window.End))).ToList(),
            SetupDurations = center.SetupTransitions.Select(transition => new SetupDuration(
                transition.FromFamily, transition.ToFamily, checked(transition.DurationMinutes * 60L))).ToList()
        };
    }

    private static List<(DateTime Start, DateTime End)> BuildShiftWindows(
        string timeZoneId,
        IReadOnlyList<CalendarShift> shifts,
        DateTime horizonStartUtc,
        DateTime horizonEndUtc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var firstLocalDate = TimeZoneInfo.ConvertTimeFromUtc(horizonStartUtc, zone).Date.AddDays(-1);
        var lastLocalDate = TimeZoneInfo.ConvertTimeFromUtc(horizonEndUtc, zone).Date.AddDays(1);
        var result = new List<(DateTime Start, DateTime End)>();
        for (var date = firstLocalDate; date <= lastLocalDate; date = date.AddDays(1))
        {
            foreach (var shift in shifts.Where(item => item.DayOfWeek == date.DayOfWeek))
            {
                var start = ToUtc(zone, date.AddMinutes(shift.StartMinute));
                var end = ToUtc(zone, date.AddMinutes(shift.EndMinute));
                start = start < horizonStartUtc ? horizonStartUtc : start;
                end = end > horizonEndUtc ? horizonEndUtc : end;
                if (end > start)
                    result.Add((start, end));
            }
        }
        return result.OrderBy(window => window.Start).ToList();
    }

    private static DateTime ToUtc(TimeZoneInfo zone, DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static List<(DateTime Start, DateTime End)> SubtractDowntimes(
        List<(DateTime Start, DateTime End)> windows,
        IEnumerable<MachineDowntime> downtimes)
    {
        foreach (var downtime in downtimes.OrderBy(item => item.StartUtc))
        {
            var next = new List<(DateTime Start, DateTime End)>();
            foreach (var window in windows)
            {
                if (downtime.EndUtc <= window.Start || downtime.StartUtc >= window.End)
                {
                    next.Add(window);
                    continue;
                }
                if (downtime.StartUtc > window.Start)
                    next.Add((window.Start, downtime.StartUtc));
                if (downtime.EndUtc < window.End)
                    next.Add((downtime.EndUtc, window.End));
            }
            windows = next;
        }
        return windows;
    }

    private static long Seconds(DateTime horizonStartUtc, DateTime valueUtc) =>
        checked((long)(valueUtc - horizonStartUtc).TotalSeconds);
}
