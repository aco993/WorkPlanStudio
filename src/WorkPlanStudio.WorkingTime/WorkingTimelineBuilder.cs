namespace WorkPlanStudio.WorkingTime;

/// <summary>
/// Turns a shift pattern, the working-time rules and a list of absences into a
/// <see cref="WorkingTimeline"/>. The rules are applied as <b>hard cuts</b> to
/// the capacity, in this order, per shift instance in an ordinary week:
/// <list type="number">
/// <item>§9 — clip everything that falls on a closed Sunday;</item>
/// <item>§3 / §6 — cap the working time of the shift (night cap when it is night work);</item>
/// <item>§5 — delay a shift whose crew has not had its rest since their previous shift;</item>
/// <item>§4 — carve the owed breaks out of the shift.</item>
/// </list>
/// Public holidays and absences are one-off exceptions layered on top. Every
/// cut is recorded as a <see cref="RuleApplication"/> so the UI can explain it.
/// Deterministic: same inputs, same timeline.
/// </summary>
public static class WorkingTimelineBuilder
{
    private const long Minute = 60;
    private const long Hour = 3600;
    private const long Day = 24 * Hour;
    private const long Week = 7 * Day;

    /// <summary>Breaks snap to this grid so a planner can read them off a clock.</summary>
    private const long BreakGridSeconds = 5 * Minute;

    /// <summary>The longest range that will be materialised — enough for any demo, small enough to stay cheap.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(5 * 366);

    /// <summary>Builds the timeline for <c>[from, to)</c>.</summary>
    public static WorkingTimeline Build(
        ShiftPattern pattern,
        WorkingTimeRules rules,
        IReadOnlyList<AbsencePeriod> absences,
        DateTime from,
        DateTime to)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(absences);
        pattern.Validate();
        rules.Validate();
        foreach (var absence in absences)
            absence.Validate();
        if (to <= from)
            throw new ArgumentException("The range end must come after its start.", nameof(to));
        if (to - from > MaxRange)
            throw new ArgumentException($"The range may span at most {MaxRange.TotalDays:0} days.", nameof(to));

        var applications = new List<RuleApplication>();
        var annotations = new List<WeekWindow>();
        var blocks = new List<Block>();

        if (!pattern.IsContinuous)
        {
            blocks = Instances(pattern);
            blocks = ClipSundays(blocks, rules, applications, annotations);
            blocks = CapWorkingTime(blocks, rules, applications);
            blocks = EnforceRest(blocks, rules, applications, annotations);
            CarveBreaks(blocks, rules, applications, annotations);
        }

        var windows = Windows(blocks);
        var gaps = Gaps(windows, annotations);
        var (exceptions, holidays) = Exceptions(rules, absences, from, to);
        int freeSundays = FreeSundaysPerYear(pattern, windows, rules);

        return new WorkingTimeline(pattern, rules, from, to, windows, gaps, exceptions, applications, holidays, freeSundays);
    }

    // ----- 1. shift instances of one week -----

    private sealed class Block
    {
        public required ShiftDefinition Shift { get; init; }
        public required DayOfWeek Day { get; init; }
        public long Start { get; set; }
        public long End { get; set; }
        public long Gross => End - Start;
        public List<(long Start, long End)> Breaks { get; } = [];
    }

    private static List<Block> Instances(ShiftPattern pattern)
    {
        var blocks = new List<Block>();
        foreach (var shift in pattern.Shifts)
        {
            for (int index = 0; index < 7; index++)
            {
                var day = (DayOfWeek)((index + 1) % 7);   // Monday first
                if (!shift.Days.Includes(day))
                    continue;

                long start = index * Day + (long)shift.Start.ToTimeSpan().TotalSeconds;
                blocks.Add(new Block { Shift = shift, Day = day, Start = start, End = start + (long)shift.Duration.TotalSeconds });
            }
        }

        return blocks;
    }

    // ----- 2. §9 Sunday -----

    private static List<Block> ClipSundays(
        List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications, List<WeekWindow> annotations)
    {
        if (rules.SundayWorkAllowed)
            return blocks;

        long shift = (long)rules.SundayBoundaryShift.TotalSeconds;
        long closedStart = 6 * Day + shift;
        long closedEnd = 7 * Day + shift;

        foreach (var (start, end) in Normalise(closedStart, closedEnd))
            annotations.Add(new WeekWindow(start, end, SegmentKind.Sunday, "", DayOf(start)));

        var result = new List<Block>();
        foreach (var block in blocks)
        {
            long original = block.Gross;
            var pieces = new List<(long, long)> { (block.Start, block.End) };

            // The closed window of this week, of the previous week (a shifted
            // boundary reaches into Monday) and of the next (a Sunday night shift).
            foreach (long offset in new[] { -Week, 0, Week })
                pieces = pieces.SelectMany(p => Subtract(p, closedStart + offset, closedEnd + offset)).ToList();

            long kept = pieces.Sum(p => p.Item2 - p.Item1);
            if (kept != original)
                applications.Add(new RuleApplication(WorkingTimeRuleId.SundayRest, block.Shift.Key, block.Day,
                    TimeSpan.FromSeconds(original), TimeSpan.FromSeconds(kept)));

            result.AddRange(pieces.Select(p => new Block { Shift = block.Shift, Day = block.Day, Start = p.Item1, End = p.Item2 }));
        }

        return result;
    }

    private static IEnumerable<(long Start, long End)> Subtract((long Start, long End) interval, long cutStart, long cutEnd)
    {
        if (cutEnd <= interval.Start || cutStart >= interval.End)
        {
            yield return interval;
            yield break;
        }

        if (interval.Start < cutStart)
            yield return (interval.Start, cutStart);
        if (cutEnd < interval.End)
            yield return (cutEnd, interval.End);
    }

    // ----- 3. §3 / §6 caps -----

    private static List<Block> CapWorkingTime(List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications)
    {
        foreach (var block in blocks)
        {
            long dayCap = (long)rules.DailyCap.TotalSeconds;
            long nightCap = (long)rules.NightCap.TotalSeconds;
            bool isNightWork = NightSeconds(block, rules) > (long)rules.NightWorkThreshold.TotalSeconds;

            long cap = isNightWork ? Math.Min(dayCap, nightCap) : dayCap;
            long net = NetWorkingSeconds(block.Gross, rules);
            if (net <= cap)
                continue;

            long newGross = GrossForNet(cap, rules);
            block.End = block.Start + newGross;

            var rule = isNightWork && nightCap < dayCap ? WorkingTimeRuleId.NightWork : WorkingTimeRuleId.MaxDailyWorkingTime;
            applications.Add(new RuleApplication(rule, block.Shift.Key, block.Day, TimeSpan.FromSeconds(net), TimeSpan.FromSeconds(cap)));
        }

        return blocks.Where(b => b.Gross > 0).ToList();
    }

    /// <summary>Seconds of the block that fall inside the nightly night window.</summary>
    private static long NightSeconds(Block block, WorkingTimeRules rules)
    {
        long nightStart = (long)rules.NightStart.ToTimeSpan().TotalSeconds;
        long nightEnd = (long)rules.NightEnd.ToTimeSpan().TotalSeconds;
        if (nightEnd <= nightStart)
            nightEnd += Day;

        long total = 0;
        for (long dayStart = -Day; dayStart <= 8 * Day; dayStart += Day)
        {
            long overlapStart = Math.Max(block.Start, dayStart + nightStart);
            long overlapEnd = Math.Min(block.End, dayStart + nightEnd);
            if (overlapEnd > overlapStart)
                total += overlapEnd - overlapStart;
        }

        return total;
    }

    /// <summary>§4 break owed for a shift of <paramref name="gross"/> seconds, consistent with the net time it leaves.</summary>
    internal static long BreakSeconds(long gross, WorkingTimeRules rules)
    {
        long six = (long)rules.MaxWorkWithoutBreak.TotalSeconds;
        long nine = 9 * Hour;
        long shortBreak = (long)rules.BreakAfterSixHours.TotalSeconds;
        long longBreak = (long)rules.BreakAfterNineHours.TotalSeconds;

        if (gross <= six)
            return 0;
        // With the short break taken, is the remaining working time still over nine hours?
        return gross - shortBreak > nine ? longBreak : shortBreak;
    }

    internal static long NetWorkingSeconds(long gross, WorkingTimeRules rules) => gross - BreakSeconds(gross, rules);

    /// <summary>The longest gross shift whose net working time does not exceed <paramref name="netCap"/>.</summary>
    internal static long GrossForNet(long netCap, WorkingTimeRules rules)
    {
        long longBreak = (long)rules.BreakAfterNineHours.TotalSeconds;
        long shortBreak = (long)rules.BreakAfterSixHours.TotalSeconds;

        foreach (long candidate in new[] { netCap + longBreak, netCap + shortBreak, netCap })
        {
            if (NetWorkingSeconds(candidate, rules) <= netCap)
                return candidate;
        }

        return netCap;
    }

    // ----- 4. §5 rest -----

    private static List<Block> EnforceRest(
        List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications, List<WeekWindow> annotations)
    {
        long rest = (long)rules.MinimumRest.TotalSeconds;
        var kept = new List<Block>();

        foreach (var crew in blocks.GroupBy(b => b.Shift.Crew, StringComparer.Ordinal))
        {
            var ordered = crew.OrderBy(b => b.Start).ToList();
            if (ordered.Count == 0)
                continue;

            // Consecutive pairs, then the wrap from the last shift of the week to
            // the first of the next: the pattern repeats, so that gap is real too.
            for (int i = 0; i < ordered.Count; i++)
            {
                var previous = i == 0 ? ordered[^1] : ordered[i - 1];
                var current = ordered[i];
                long previousEnd = i == 0 ? previous.End - Week : previous.End;
                if (ordered.Count == 1)
                    previousEnd = current.End - Week;

                long gap = current.Start - previousEnd;
                if (gap >= rest)
                    continue;

                long delayedStart = previousEnd + rest;
                applications.Add(new RuleApplication(WorkingTimeRuleId.RestPeriod, current.Shift.Key, current.Day,
                    TimeSpan.FromSeconds(gap), TimeSpan.FromSeconds(rest)));

                foreach (var (start, end) in Normalise(current.Start, Math.Min(delayedStart, current.End)))
                    annotations.Add(new WeekWindow(start, end, SegmentKind.Rest, current.Shift.Key, current.Day));

                current.Start = delayedStart;
            }

            kept.AddRange(ordered.Where(b => b.Gross > 0));
        }

        return kept.OrderBy(b => b.Start).ToList();
    }

    // ----- 5. §4 breaks -----

    private static void CarveBreaks(
        List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications, List<WeekWindow> annotations)
    {
        long minPiece = (long)rules.MinimumBreakPiece.TotalSeconds;
        long shortBreak = (long)rules.BreakAfterSixHours.TotalSeconds;
        long maxStretch = (long)rules.MaxWorkWithoutBreak.TotalSeconds;

        foreach (var block in blocks)
        {
            long total = BreakSeconds(block.Gross, rules);
            if (total == 0)
                continue;

            // 30 → one piece; 45 → 30 + 15. More pieces only if a stretch would
            // otherwise exceed the six-hour limit.
            var pieces = total > shortBreak && total - shortBreak >= minPiece
                ? new List<long> { shortBreak, total - shortBreak }
                : [total];

            while (StretchFor(block.Gross, total, pieces.Count) > maxStretch && pieces.Count < total / minPiece)
            {
                // Split the largest piece in two.
                int largest = pieces.IndexOf(pieces.Max());
                long half = pieces[largest] / 2 / Minute * Minute;
                if (half < minPiece)
                    break;
                pieces[largest] -= half;
                pieces.Add(half);
            }

            long stretch = StretchFor(block.Gross, total, pieces.Count);
            long cursor = block.Start;
            foreach (long piece in pieces)
            {
                long breakStart = (cursor + stretch) / BreakGridSeconds * BreakGridSeconds;
                breakStart = Math.Max(breakStart, cursor);
                long breakEnd = Math.Min(breakStart + piece, block.End);
                if (breakEnd <= breakStart)
                    break;
                block.Breaks.Add((breakStart, breakEnd));
                foreach (var (start, end) in Normalise(breakStart, breakEnd))
                    annotations.Add(new WeekWindow(start, end, SegmentKind.Break, block.Shift.Key, block.Day));
                cursor = breakEnd;
            }

            applications.Add(new RuleApplication(WorkingTimeRuleId.Breaks, block.Shift.Key, block.Day,
                TimeSpan.FromSeconds(block.Gross), TimeSpan.FromSeconds(total)));
        }
    }

    private static long StretchFor(long gross, long totalBreak, int pieceCount) =>
        (gross - totalBreak) / (pieceCount + 1);

    // ----- 6. windows and gaps -----

    private static List<WeekWindow> Windows(List<Block> blocks)
    {
        var windows = new List<WeekWindow>();
        foreach (var block in blocks)
        {
            long cursor = block.Start;
            foreach (var (breakStart, breakEnd) in block.Breaks.OrderBy(b => b.Start))
            {
                if (breakStart > cursor)
                    windows.AddRange(Normalise(cursor, breakStart).Select(w => new WeekWindow(w.Start, w.End, SegmentKind.Working, block.Shift.Key, block.Day)));
                cursor = breakEnd;
            }

            if (cursor < block.End)
                windows.AddRange(Normalise(cursor, block.End).Select(w => new WeekWindow(w.Start, w.End, SegmentKind.Working, block.Shift.Key, block.Day)));
        }

        windows.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));

        // Adjacent or overlapping windows (two shifts back to back) merge into one
        // so the engine sees a single open stretch and a long operation can span
        // the hand-over.
        var merged = new List<WeekWindow>();
        foreach (var window in windows)
        {
            if (merged.Count > 0 && window.StartSeconds <= merged[^1].EndSeconds)
            {
                var last = merged[^1];
                merged[^1] = last with
                {
                    EndSeconds = Math.Max(last.EndSeconds, window.EndSeconds),
                    Label = last.Label == window.Label ? last.Label : $"{last.Label}+{window.Label}"
                };
            }
            else
            {
                merged.Add(window);
            }
        }

        return merged;
    }

    private static List<WeekWindow> Gaps(List<WeekWindow> windows, List<WeekWindow> annotations)
    {
        if (windows.Count == 0)
            return [];

        // The complement of the windows over one week …
        var complement = new List<(long Start, long End)>();
        long cursor = 0;
        foreach (var window in windows)
        {
            if (window.StartSeconds > cursor)
                complement.Add((cursor, window.StartSeconds));
            cursor = Math.Max(cursor, window.EndSeconds);
        }

        if (cursor < Week)
            complement.Add((cursor, Week));

        // … classified by the annotation that covers each piece. Sunday beats a
        // break beats a rest delay; anything unexplained is simply off-shift.
        static int Priority(SegmentKind kind) => kind switch
        {
            SegmentKind.Sunday => 3,
            SegmentKind.Break => 2,
            SegmentKind.Rest => 1,
            _ => 0
        };

        var gaps = new List<WeekWindow>();
        foreach (var (gapStart, gapEnd) in complement)
        {
            var cuts = new SortedSet<long> { gapStart, gapEnd };
            foreach (var annotation in annotations)
            {
                if (annotation.StartSeconds > gapStart && annotation.StartSeconds < gapEnd) cuts.Add(annotation.StartSeconds);
                if (annotation.EndSeconds > gapStart && annotation.EndSeconds < gapEnd) cuts.Add(annotation.EndSeconds);
            }

            var points = cuts.ToList();
            for (int i = 0; i < points.Count - 1; i++)
            {
                long start = points[i], end = points[i + 1];
                var best = annotations
                    .Where(a => a.StartSeconds <= start && a.EndSeconds >= end)
                    .OrderByDescending(a => Priority(a.Kind))
                    .FirstOrDefault();

                var kind = best?.Kind ?? SegmentKind.OffShift;
                var label = best?.Label ?? "";
                if (gaps.Count > 0 && gaps[^1].EndSeconds == start && gaps[^1].Kind == kind && gaps[^1].Label == label)
                    gaps[^1] = gaps[^1] with { EndSeconds = end };
                else
                    gaps.Add(new WeekWindow(start, end, kind, label, DayOf(start)));
            }
        }

        return gaps;
    }

    /// <summary>The weekday a week-relative second falls on (Monday = 0).</summary>
    private static DayOfWeek DayOf(long weekSeconds) => (DayOfWeek)((int)(weekSeconds / Day % 7 + 1) % 7);

    /// <summary>Folds a week-relative interval into <c>[0, Week)</c>, splitting at the boundary if needed.</summary>
    private static IEnumerable<(long Start, long End)> Normalise(long start, long end)
    {
        if (end <= start)
            yield break;

        long shift = start >= Week ? -Week : start < 0 ? Week : 0;
        start += shift;
        end += shift;

        if (end <= Week)
        {
            yield return (start, end);
        }
        else
        {
            yield return (start, Week);
            yield return (0, end - Week);
        }
    }

    // ----- 7. exceptions -----

    private static (IReadOnlyList<TimelineSegment> Segments, IReadOnlyList<PublicHoliday> Holidays) Exceptions(
        WorkingTimeRules rules, IReadOnlyList<AbsencePeriod> absences, DateTime from, DateTime to)
    {
        var segments = new List<TimelineSegment>();
        var holidays = new List<PublicHoliday>();

        if (!rules.HolidayWorkAllowed)
        {
            var boundary = rules.SundayBoundaryShift;
            foreach (var holiday in GermanHolidays.Between(
                         DateOnly.FromDateTime(from.Date.AddDays(-1)), DateOnly.FromDateTime(to.Date), rules.State, rules.IncludePartialHolidays))
            {
                var start = holiday.Date.ToDateTime(TimeOnly.MinValue) + boundary;
                var end = start.AddDays(1);
                if (end <= from || start >= to)
                    continue;
                holidays.Add(holiday);
                segments.Add(new TimelineSegment(start, end, SegmentKind.Holiday, holiday.Key));
            }
        }

        foreach (var absence in absences)
        {
            if (absence.End <= from || absence.Start >= to)
                continue;
            segments.Add(new TimelineSegment(absence.Start, absence.End, SegmentKind.Absence,
                string.IsNullOrWhiteSpace(absence.Label) ? absence.Kind.ToString() : absence.Label));
        }

        segments.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        // Overlaps merge into one closed stretch; the first cause keeps the kind,
        // the labels are joined so nothing is hidden.
        var merged = new List<TimelineSegment>();
        foreach (var segment in segments)
        {
            var clipped = segment with
            {
                Start = segment.Start < from ? from : segment.Start,
                End = segment.End > to ? to : segment.End
            };

            if (merged.Count > 0 && clipped.Start < merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with
                {
                    End = clipped.End > last.End ? clipped.End : last.End,
                    Label = last.Label.Contains(clipped.Label, StringComparison.Ordinal) ? last.Label : $"{last.Label}; {clipped.Label}"
                };
            }
            else
            {
                merged.Add(clipped);
            }
        }

        return (merged, holidays);
    }

    private static int FreeSundaysPerYear(ShiftPattern pattern, List<WeekWindow> windows, WorkingTimeRules rules)
    {
        // A continuous pattern is an unattended machine: nobody's Sunday is at
        // stake, and §11 is about people. It reports 0 free Sundays so a UI can
        // still say "runs every Sunday"; ViolatesFreeSundays ignores it.
        if (pattern.IsContinuous)
            return 0;

        long shift = (long)rules.SundayBoundaryShift.TotalSeconds;
        bool sundayWorked = windows.Any(w =>
            (w.StartSeconds < 7 * Day + shift && w.EndSeconds > 6 * Day + shift) ||
            (shift > 0 && w.StartSeconds < shift));

        return sundayWorked ? 0 : 52;
    }
}
