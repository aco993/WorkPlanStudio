namespace WorkPlanStudio.WorkingTime;

/// <summary>What an averaging result was computed from.</summary>
public enum AveragingBasis
{
    /// <summary>At least one full reference period fits inside the timeline's range, so the average is measured.</summary>
    MaterialisedRange,

    /// <summary>
    /// The range is shorter than the reference period, so the average is the one
    /// the repeating week produces if it goes on — which is what a plant that
    /// keeps running this pattern will in fact be measured against.
    /// </summary>
    ProjectedFromWeeklyPattern
}

/// <summary>
/// What one crew works on one calendar day, once holidays and absences are out.
/// A shift that crosses midnight counts on the day the crew reported for.
/// </summary>
/// <param name="Date">The calendar day.</param>
/// <param name="Crew">The crew.</param>
/// <param name="ClockWorkingTime">The difference the plant's clock shows.</param>
/// <param name="ActualWorkingTime">Hours really elapsed. Equal to the clock time except where a zone was supplied and the clocks changed.</param>
/// <param name="NightWork">Whether §2 (4) makes any of it night work, so that §6 (2) caps the day.</param>
public sealed record CrewWorkingDay(DateOnly Date, string Crew, TimeSpan ClockWorkingTime, TimeSpan ActualWorkingTime, bool NightWork);

/// <summary>A day on which a crew works longer than the section allows.</summary>
/// <param name="Rule">§3 or §6 (2).</param>
/// <param name="Crew">Whose day it is.</param>
/// <param name="Date">The calendar day.</param>
/// <param name="Measured">Hours really worked.</param>
/// <param name="Limit">What the section allows.</param>
public sealed record WorkingTimeBreach(WorkingTimeRuleId Rule, string Crew, DateOnly Date, TimeSpan Measured, TimeSpan Limit);

/// <summary>
/// The rolling average §3 sentence 2 (and §6 (2)) makes the condition of the
/// extended working day: taking the 10-hour day is lawful only while the
/// werktäglich average over the reference period stays at eight hours.
/// </summary>
/// <param name="Rule">Which section this average belongs to.</param>
/// <param name="Crew">Whose hours were averaged.</param>
/// <param name="Basis">Measured over the range, or projected from the repeating week.</param>
/// <param name="WindowStart">First day of the worst window found.</param>
/// <param name="WindowEnd">Last day of that window, inclusive.</param>
/// <param name="TotalWorkingTime">Hours worked inside it.</param>
/// <param name="Werktage">Working days (Monday to Saturday) the window contains — the divisor the section uses. Public holidays count.</param>
/// <param name="AverageWerktaeglichHours">The average this produces.</param>
/// <param name="Limit">The average the section allows, normally 8 h.</param>
/// <param name="FirstBreachDate">The last day of the earliest window that exceeds the limit, or null when none does.</param>
/// <param name="CompensationDaysOwed">Full working days that have to come out of the window to bring the average back.</param>
public sealed record AveragingResult(
    WorkingTimeRuleId Rule,
    string Crew,
    AveragingBasis Basis,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    TimeSpan TotalWorkingTime,
    int Werktage,
    double AverageWerktaeglichHours,
    TimeSpan Limit,
    DateOnly? FirstBreachDate,
    int CompensationDaysOwed)
{
    /// <summary>True when the plan does not keep the promise the extension is conditional on.</summary>
    public bool ExceedsAverage => FirstBreachDate is not null;
}

/// <summary>
/// Everything about a plan that only a calendar can tell you: what each crew
/// works day by day, where a daily cap is exceeded once real elapsed time is
/// counted, and whether the averaging duty the extended working day is
/// conditional on is actually kept.
/// </summary>
/// <param name="Days">Per crew and calendar day, sorted.</param>
/// <param name="Averaging">One result per crew per averaged section.</param>
/// <param name="Breaches">Days over the §3 or §6 (2) cap.</param>
public sealed record WorkingTimeCompliance(
    IReadOnlyList<CrewWorkingDay> Days,
    IReadOnlyList<AveragingResult> Averaging,
    IReadOnlyList<WorkingTimeBreach> Breaches)
{
    /// <summary>True when nothing above is out of order.</summary>
    public bool IsCompliant => Breaches.Count == 0 && !Averaging.Any(a => a.ExceedsAverage);
}

/// <summary>
/// Walks a built timeline day by day and answers the questions the weekly
/// pattern cannot: §3 sentence 2 and §6 (2) are duties about a <i>period</i>, and
/// the two days a year the clocks change are only visible on a calendar.
/// </summary>
internal static class WorkingTimeEvaluator
{
    private const long Day = 24 * 3600;

    /// <summary>Monday to Saturday: the divisor §3 means by "werktäglich".</summary>
    private static bool IsWerktag(DateOnly date) => date.DayOfWeek != DayOfWeek.Sunday;

    /// <summary>
    /// Werktage the crew was released from work for the whole day — a public
    /// holiday, a shutdown — leave the divisor along with the hours. Counting
    /// them as zero-hour working days would let a plant mend its average by
    /// closing for Christmas, which is the opposite of what §3 sentence 2 is for.
    /// </summary>
    private static bool[] Neutralised(WorkingTimeline timeline, DateOnly firstDate, int dayCount)
    {
        var neutral = new bool[dayCount];
        foreach (var exception in timeline.Exceptions)
        {
            var first = DateOnly.FromDateTime(exception.Start.Date);
            var last = DateOnly.FromDateTime(exception.End.AddSeconds(-1).Date);
            for (var date = first; date <= last; date = date.AddDays(1))
            {
                int index = date.DayNumber - firstDate.DayNumber;
                if (index < 0 || index >= dayCount || !IsWerktag(date))
                    continue;

                var dayStart = date.ToDateTime(TimeOnly.MinValue);
                if (exception.Start <= dayStart && exception.End >= dayStart.AddDays(1))
                    neutral[index] = true;
            }
        }

        return neutral;
    }

    internal static WorkingTimeCompliance Evaluate(WorkingTimeline timeline, TimeZoneInfo? zone)
    {
        var firstDate = DateOnly.FromDateTime(timeline.From.Date);
        var lastDate = DateOnly.FromDateTime(timeline.To.Date);
        int dayCount = lastDate.DayNumber - firstDate.DayNumber;
        if (dayCount <= 0 || timeline.CrewShifts.Count == 0)
            return new WorkingTimeCompliance([], [], []);

        // clock[crew][dayIndex] and actual[crew][dayIndex], in seconds.
        var clock = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var actual = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var night = new Dictionary<string, bool[]>(StringComparer.Ordinal);
        foreach (var crew in timeline.Crews)
        {
            clock[crew] = new double[dayCount];
            actual[crew] = new double[dayCount];
            night[crew] = new bool[dayCount];
        }

        var weekStart = timeline.From.Date.AddDays(-WorkingTimeline.WeekdayIndex(timeline.From.DayOfWeek));
        while (weekStart < timeline.To)
        {
            foreach (var instance in timeline.CrewShifts)
            {
                var date = DateOnly.FromDateTime(weekStart.AddDays(WorkingTimeline.WeekdayIndex(instance.Day)));
                int index = date.DayNumber - firstDate.DayNumber;
                if (index < 0 || index >= dayCount)
                    continue;

                foreach (var span in instance.WorkingSpans)
                {
                    var spanStart = weekStart.AddSeconds(span.StartSeconds);
                    var spanEnd = weekStart.AddSeconds(span.EndSeconds);
                    if (spanStart < timeline.From) spanStart = timeline.From;
                    if (spanEnd > timeline.To) spanEnd = timeline.To;
                    if (spanEnd <= spanStart)
                        continue;

                    foreach (var (openStart, openEnd) in Subtract(spanStart, spanEnd, timeline.Exceptions))
                    {
                        clock[instance.Crew][index] += (openEnd - openStart).TotalSeconds;
                        actual[instance.Crew][index] += zone is null
                            ? (openEnd - openStart).TotalSeconds
                            : PlantTime.RealElapsed(openStart, openEnd, zone).TotalSeconds;
                        if (instance.NightWork)
                            night[instance.Crew][index] = true;
                    }
                }
            }

            weekStart = weekStart.AddDays(7);
        }

        var days = new List<CrewWorkingDay>();
        var breaches = new List<WorkingTimeBreach>();
        long dayCap = (long)timeline.Rules.DailyCap.TotalSeconds;
        long nightCap = (long)timeline.Rules.NightCap.TotalSeconds;

        for (int index = 0; index < dayCount; index++)
        {
            var date = firstDate.AddDays(index);
            foreach (var crew in timeline.Crews)
            {
                double seconds = clock[crew][index];
                double real = actual[crew][index];
                if (seconds <= 0 && real <= 0)
                    continue;

                bool isNight = night[crew][index];
                days.Add(new CrewWorkingDay(date, crew, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(real), isNight));

                long limit = isNight ? Math.Min(dayCap, nightCap) : dayCap;
                if (real > limit)
                {
                    breaches.Add(new WorkingTimeBreach(
                        isNight && nightCap < dayCap ? WorkingTimeRuleId.NightWork : WorkingTimeRuleId.MaxDailyWorkingTime,
                        crew, date, TimeSpan.FromSeconds(real), TimeSpan.FromSeconds(limit)));
                }
            }
        }

        var neutral = Neutralised(timeline, firstDate, dayCount);
        var averaging = new List<AveragingResult>();
        foreach (var crew in timeline.Crews)
        {
            averaging.Add(Average(
                timeline, crew, WorkingTimeRuleId.MaxDailyWorkingTime, actual[crew], neutral, firstDate, dayCount,
                WindowDays(timeline.Rules.AveragingWindow), timeline.Rules.MaxDailyWorkingTime, WeeksIn(timeline.Rules.AveragingWindow)));

            // §6 (2) averages night work over four weeks against the same eight
            // werktäglich hours, so a crew that does any night work is measured
            // twice — once for the day it works, once for the nights.
            if (night[crew].Any(n => n))
            {
                averaging.Add(Average(
                    timeline, crew, WorkingTimeRuleId.NightWork, actual[crew], neutral, firstDate, dayCount,
                    28, timeline.Rules.MaxNightWorkingTime, 4));
            }
        }

        return new WorkingTimeCompliance(days, averaging, breaches);
    }

    private static int WindowDays(AveragingWindow window) => window switch
    {
        AveragingWindow.SixCalendarMonths => 182,
        _ => 168
    };

    private static int WeeksIn(AveragingWindow window) => window switch
    {
        AveragingWindow.SixCalendarMonths => 26,
        _ => 24
    };

    private static AveragingResult Average(
        WorkingTimeline timeline,
        string crew,
        WorkingTimeRuleId rule,
        double[] seconds,
        bool[] neutral,
        DateOnly firstDate,
        int dayCount,
        int windowDays,
        TimeSpan limit,
        int weeks)
    {
        if (dayCount < windowDays)
            return Project(timeline, crew, rule, firstDate, windowDays, limit, weeks);

        var prefix = new double[dayCount + 1];
        var werktage = new int[dayCount + 1];
        for (int index = 0; index < dayCount; index++)
        {
            prefix[index + 1] = prefix[index] + seconds[index];
            werktage[index + 1] = werktage[index] + (IsWerktag(firstDate.AddDays(index)) && !neutral[index] ? 1 : 0);
        }

        double limitSeconds = limit.TotalSeconds;
        double worstAverage = -1;
        int worstStart = 0;
        int worstDivisor = 1;
        double worstTotal = 0;
        DateOnly? firstBreach = null;

        for (int start = 0; start + windowDays <= dayCount; start++)
        {
            int end = start + windowDays;
            double total = prefix[end] - prefix[start];
            int divisor = werktage[end] - werktage[start];
            if (divisor == 0)
                continue;

            double average = total / divisor;
            if (average > worstAverage)
            {
                worstAverage = average;
                worstStart = start;
                worstDivisor = divisor;
                worstTotal = total;
            }

            if (firstBreach is null && average > limitSeconds)
                firstBreach = firstDate.AddDays(end - 1);
        }

        return Build(rule, crew, AveragingBasis.MaterialisedRange,
            firstDate.AddDays(worstStart), firstDate.AddDays(worstStart + windowDays - 1),
            worstTotal, worstDivisor, limit, firstBreach);
    }

    /// <summary>
    /// The range is shorter than the reference period, so nothing can be measured
    /// yet. The pattern repeats, though, and that is what the plant will be held
    /// to: the projection says what the average becomes once the window closes,
    /// and when that is.
    /// </summary>
    private static AveragingResult Project(
        WorkingTimeline timeline, string crew, WorkingTimeRuleId rule, DateOnly firstDate, int windowDays, TimeSpan limit, int weeks)
    {
        double weekly = timeline.WeeklyWorkingSecondsFor(crew);
        double total = weekly * weeks;
        int divisor = weeks * 6;   // six Werktage in each week of the reference period
        double average = divisor == 0 ? 0 : total / divisor;
        var windowEnd = firstDate.AddDays(windowDays - 1);
        return Build(rule, crew, AveragingBasis.ProjectedFromWeeklyPattern,
            firstDate, windowEnd, total, divisor, limit, average > limit.TotalSeconds ? windowEnd : null);
    }

    private static AveragingResult Build(
        WorkingTimeRuleId rule, string crew, AveragingBasis basis, DateOnly windowStart, DateOnly windowEnd,
        double totalSeconds, int divisor, TimeSpan limit, DateOnly? firstBreach)
    {
        double average = divisor == 0 ? 0 : totalSeconds / divisor;
        double allowance = limit.TotalSeconds * divisor;
        int compensation = totalSeconds > allowance && limit > TimeSpan.Zero
            ? (int)Math.Ceiling((totalSeconds - allowance) / limit.TotalSeconds)
            : 0;

        return new AveragingResult(
            rule, crew, basis, windowStart, windowEnd,
            TimeSpan.FromSeconds(totalSeconds), divisor, average / 3600, limit, firstBreach, compensation);
    }

    /// <summary>What is left of <c>[start, end)</c> once the exceptions are taken out.</summary>
    private static List<(DateTime Start, DateTime End)> Subtract(
        DateTime start, DateTime end, IReadOnlyList<TimelineSegment> exceptions)
    {
        var open = new List<(DateTime Start, DateTime End)>();
        var cursor = start;
        foreach (var exception in exceptions)
        {
            if (exception.End <= cursor)
                continue;
            if (exception.Start >= end)
                break;
            if (exception.Start > cursor)
                open.Add((cursor, exception.Start));
            if (exception.End > cursor)
                cursor = exception.End;
            if (cursor >= end)
                return open;
        }

        if (cursor < end)
            open.Add((cursor, end));
        return open;
    }
}
