using WorkPlanStudio.Models;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services;

/// <summary>
/// Everything the scheduler needs to know about <i>when</i> the shop works: the
/// plant's rules and, per work center, its shift pattern and absences. Pure —
/// the service layer loads it, the mapper turns it into engine calendars, and
/// the page reads the annotated timelines back for the Gantt shading.
/// </summary>
public sealed record ShopCalendar(
    WorkingTimeRules Rules,
    IReadOnlyDictionary<int, ShiftPattern> PatternByWorkCenter,
    IReadOnlyDictionary<int, IReadOnlyList<AbsencePeriod>> AbsencesByWorkCenter)
{
    /// <summary>How far past the horizon holidays and absences are materialised.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromDays(400);

    /// <summary>
    /// A calendar where every work center runs around the clock, Sundays and
    /// holidays included — the behaviour before working time was modelled.
    /// </summary>
    public static ShopCalendar Unconstrained { get; } = new(
        WorkingTimeRules.Statutory with { SundayWorkAllowed = true, HolidayWorkAllowed = true },
        new Dictionary<int, ShiftPattern>(),
        new Dictionary<int, IReadOnlyList<AbsencePeriod>>());

    /// <summary>Builds the calendar from stored entities.</summary>
    public static ShopCalendar From(PlantSettings settings, IEnumerable<WorkCenter> centers, IEnumerable<WorkCenterAbsence> absences)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var patterns = centers.ToDictionary(
            c => c.Id,
            c => ShiftPatterns.ByKey(c.ShiftPatternKey) ?? ShiftPatterns.Continuous);

        var byCenter = absences
            .GroupBy(a => a.WorkCenterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AbsencePeriod>)g.Select(a => a.ToPeriod()).ToList());

        return new ShopCalendar(settings.ToRules(), patterns, byCenter);
    }

    /// <summary>The pattern a work center is staffed by (continuous when unknown).</summary>
    public ShiftPattern PatternFor(int workCenterId) =>
        PatternByWorkCenter.TryGetValue(workCenterId, out var pattern) ? pattern : ShiftPatterns.Continuous;

    /// <summary>The absences of a work center (none when unknown).</summary>
    public IReadOnlyList<AbsencePeriod> AbsencesFor(int workCenterId) =>
        AbsencesByWorkCenter.TryGetValue(workCenterId, out var list) ? list : [];

    /// <summary>
    /// The annotated timeline of one work center from the horizon onwards. Holidays
    /// and absences are materialised for <see cref="Lookahead"/>; the pattern
    /// itself repeats forever.
    /// </summary>
    public WorkingTimeline TimelineFor(int workCenterId, DateTime horizon) =>
        WorkingTimelineBuilder.Build(
            PatternFor(workCenterId),
            Rules,
            AbsencesFor(workCenterId),
            horizon.Date,
            horizon.Date + Lookahead);
}
