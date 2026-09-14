namespace WorkPlanStudio.WorkingTime;

/// <summary>
/// Turns a shift pattern, the working-time rules and a list of absences into a
/// <see cref="WorkingTimeline"/>. The rules are applied as <b>hard cuts</b> to
/// the capacity, in this order, per shift instance in an ordinary week:
/// <list type="number">
/// <item>§9 — clip everything that falls on a closed Sunday;</item>
/// <item>§3 / §6 — cap the working time of each <i>crew and calendar day</i>
/// (night cap when any of that day's work is night work);</item>
/// <item>§5 — delay a shift whose crew has not had its rest since their
/// previous shift, swept until the ring a repeating week makes settles;</item>
/// <item>§3 / §6 again — a delayed shift can land in a calendar day that already
/// has its hours;</item>
/// <item>§4 — carve the owed breaks out of the shift.</item>
/// </list>
/// Public holidays and absences are one-off exceptions layered on top. Every
/// cut is recorded as a <see cref="RuleApplication"/> so the UI can explain it,
/// and the obligations a permission carries with it (§5 (2) compensation,
/// §11 (2)/(3) replacement rest) are recorded the same way rather than left
/// silent. Deterministic: same inputs, same timeline.
/// <para>
/// All times are plant-local wall clock; see <see cref="PlantTime"/>. The
/// averaging duties of §3 sentence 2 and §6 (2) are date-dependent and therefore
/// not pattern cuts — <see cref="WorkingTimeline.Evaluate"/> computes those.
/// </para>
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
    /// <param name="pattern">How the work center is staffed.</param>
    /// <param name="rules">The working-time rules in force.</param>
    /// <param name="absences">One-off closures.</param>
    /// <param name="from">Start of the range, plant-local wall clock.</param>
    /// <param name="to">End of the range (exclusive), plant-local wall clock.</param>
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

        // Every DateTime crossing this boundary is read as a plant-local clock
        // reading and re-stamped, so no record downstream can end up with one end
        // in UTC and the other in nobody's time zone.
        from = PlantTime.Wall(from);
        to = PlantTime.Wall(to);
        var wallAbsences = new List<AbsencePeriod>(absences.Count);
        foreach (var absence in absences)
        {
            absence.Validate();
            wallAbsences.Add(absence.AsWallClock());
        }

        if (to <= from)
            throw new ArgumentException("The range end must come after its start.", nameof(to));
        if (to - from > MaxRange)
            throw new ArgumentException($"The range may span at most {MaxRange.TotalDays:0} days.", nameof(to));
        if (from.Year < GermanHolidays.MinYear)
            throw new ArgumentOutOfRangeException(nameof(from), from, $"The holiday tables start in {GermanHolidays.MinYear}.");
        if (to.Year > GermanHolidays.MaxYear)
            throw new ArgumentOutOfRangeException(nameof(to), to, $"The holiday tables end in {GermanHolidays.MaxYear}.");

        var applications = new List<RuleApplication>();
        var annotations = new List<WeekWindow>();
        var blocks = new List<Block>();

        if (!pattern.IsContinuous)
        {
            blocks = Instances(pattern);
            WarnIfNotMultiShift(pattern, blocks, rules, applications);
            blocks = ClipSundays(blocks, rules, applications, annotations);
            blocks = CapWorkingTime(blocks, rules, applications);
            blocks = EnforceRest(blocks, rules, applications, annotations);

            // §5 can delay a shift past midnight, and the calendar day it lands
            // in may already have its hours. Capping again is cheap, changes
            // nothing where the delay stayed inside the day, and only ever
            // shortens - so it cannot undo the rest it was given.
            blocks = CapWorkingTime(blocks, rules, applications);
            CarveBreaks(blocks, rules, applications, annotations);
        }

        var crewShifts = CrewShifts(blocks, rules);
        var windows = Windows(blocks);
        List<WeekWindow> gaps = pattern.IsContinuous ? [] : Gaps(windows, annotations);
        WeekCapacity capacity = pattern.IsContinuous
            ? new WeekCapacity.Unconstrained()
            : WeekCapacity.FromWindows(windows, CuttingRules(applications));
        var (exceptions, holidays) = Exceptions(rules, wallAbsences, from, to);

        ReportReplacementRestDays(pattern, rules, crewShifts, from, to, applications);

        return new WorkingTimeline(pattern, rules, from, to, capacity, gaps, crewShifts, exceptions, applications, holidays);
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

    /// <summary>
    /// §9 (2) reserves the moved Sunday boundary for "mehrschichtige Betriebe mit
    /// regelmäßiger Tag- und Nachtschicht". A single-shift plant that claims it is
    /// taking a privilege it does not have, and silence would be the app agreeing.
    /// </summary>
    private static void WarnIfNotMultiShift(
        ShiftPattern pattern, List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications)
    {
        if (rules.SundayBoundaryShift == TimeSpan.Zero)
            return;

        long threshold = (long)rules.NightWorkThreshold.TotalSeconds;
        bool hasNight = blocks.Any(b => NightSeconds(b, rules) > threshold);
        bool hasDay = blocks.Any(b => NightSeconds(b, rules) <= threshold);
        if (pattern.Shifts.Count >= 2 && hasNight && hasDay)
            return;

        var claimed = rules.SundayBoundaryShift < TimeSpan.Zero ? -rules.SundayBoundaryShift : rules.SundayBoundaryShift;
        applications.Add(new RuleApplication(
            WorkingTimeRuleId.MultiShiftRequirement, pattern.Key, DayOfWeek.Sunday, claimed, TimeSpan.Zero));
    }

    private static List<Block> ClipSundays(
        List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications, List<WeekWindow> annotations)
    {
        if (rules.SundayWorkAllowed)
            return blocks;

        // The shift may be negative: §9 (2) moves the closed day forward or back.
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

    /// <summary>
    /// §3 caps "die werktägliche Arbeitszeit der Arbeitnehmer" — what one crew
    /// works on one calendar day, however many blocks that is. Capping each block
    /// on its own lets a crew work 00:00–06:00 and 17:00–23:00 on the same Monday,
    /// twelve hours, two over even the extended ceiling, without a word.
    /// <para>
    /// A block that crosses midnight counts on the day it starts: that is the day
    /// the crew reported for, and it is the same convention
    /// <see cref="WeekWindow.Day"/> uses.
    /// </para>
    /// </summary>
    private static List<Block> CapWorkingTime(List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications)
    {
        long dayCap = (long)rules.DailyCap.TotalSeconds;
        long nightCap = (long)rules.NightCap.TotalSeconds;
        long threshold = (long)rules.NightWorkThreshold.TotalSeconds;

        foreach (var group in blocks
                     .GroupBy(b => (b.Shift.Crew, DayIndex: b.Start / Day), CrewDayComparer.Instance)
                     .OrderBy(g => g.Key.Crew, StringComparer.Ordinal).ThenBy(g => g.Key.DayIndex))
        {
            var ordered = group.OrderBy(b => b.Start).ToList();
            bool nightWork = ordered.Any(b => NightSeconds(b, rules) > threshold);
            long cap = nightWork ? Math.Min(dayCap, nightCap) : dayCap;

            long net = ordered.Sum(b => Net(b.Gross, rules));
            if (net <= cap)
                continue;

            // Trim from the end of the day: the crew stays on the blocks it has
            // already started and goes home early on the last one.
            long allowance = cap;
            foreach (var block in ordered)
            {
                long blockNet = Net(block.Gross, rules);
                if (blockNet <= allowance)
                {
                    allowance -= blockNet;
                    continue;
                }

                long newGross = (long)rules.GrossForNet(TimeSpan.FromSeconds(allowance)).TotalSeconds;
                block.End = block.Start + Math.Min(newGross, block.Gross);
                allowance = 0;
            }

            var last = ordered[^1];
            var rule = nightWork && nightCap < dayCap ? WorkingTimeRuleId.NightWork : WorkingTimeRuleId.MaxDailyWorkingTime;
            applications.Add(new RuleApplication(rule, last.Shift.Key, last.Day,
                TimeSpan.FromSeconds(net), TimeSpan.FromSeconds(cap)));
        }

        return blocks.Where(b => b.Gross > 0).ToList();
    }

    private sealed class CrewDayComparer : IEqualityComparer<(string Crew, long DayIndex)>
    {
        public static readonly CrewDayComparer Instance = new();

        public bool Equals((string Crew, long DayIndex) x, (string Crew, long DayIndex) y) =>
            x.DayIndex == y.DayIndex && string.Equals(x.Crew, y.Crew, StringComparison.Ordinal);

        public int GetHashCode((string Crew, long DayIndex) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Crew), obj.DayIndex);
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

    /// <summary>The working time a gross presence leaves, in seconds — §4 solved on the net, not on the span.</summary>
    private static long Net(long gross, WorkingTimeRules rules) =>
        (long)rules.NetWorkingTime(TimeSpan.FromSeconds(gross)).TotalSeconds;

    // ----- 4. §5 rest -----

    private static List<Block> EnforceRest(
        List<Block> blocks, WorkingTimeRules rules, List<RuleApplication> applications, List<WeekWindow> annotations)
    {
        long rest = (long)rules.MinimumRest.TotalSeconds;
        var kept = new List<Block>();

        foreach (var crew in blocks.GroupBy(b => b.Shift.Crew, StringComparer.Ordinal))
        {
            var live = crew.Where(b => b.Gross > 0).ToList();
            if (live.Count == 0)
                continue;

            // Where each shift stood before §5 touched it, and the rest it was
            // first found short of: the planner is told once per shift, however
            // many sweeps it took to settle.
            var originalStart = live.ToDictionary(b => b, b => b.Start);
            var firstGap = new Dictionary<Block, long>();
            var shortened = new List<Block>();

            // The pattern repeats, so the chain is a ring, and a single pass
            // measures it against a picture that the pass itself invalidates:
            // delaying the first shift of the week shortens the rest before the
            // second, and a shift the delay pushed past its own end is *gone* —
            // which hands its successor an earlier predecessor and can leave it
            // with less rest than was just measured for it. (Found by the
            // property test: a Sunday shift clipped into Monday, the Monday
            // shift it collided with delayed out of existence, and the 19:00
            // shift behind it left with ten hours.) So sweep until nothing
            // moves. Starts only ever grow and a shift that outgrows its own end
            // is dropped, so this settles; the bound is belt and braces, and the
            // check after it is what makes the invariant hold either way.
            int limit = live.Count + 1;
            for (int sweep = 0; sweep < limit; sweep++)
            {
                live.Sort(static (a, b) => a.Start.CompareTo(b.Start));
                bool moved = false;

                // "When did this crew last stop working" is the greatest end so
                // far, not the end of the block that started last - two shifts of
                // one crew can start together, and the shorter one is not where
                // the rest began.
                long previousEnd = live.Max(b => b.End) - Week;
                foreach (var current in live)
                {
                    long gap = current.Start - previousEnd;
                    if (gap < rest)
                    {
                        if (firstGap.TryAdd(current, gap))
                            shortened.Add(current);

                        current.Start = previousEnd + rest;
                        moved = true;
                    }

                    // A block the delay killed is not where anybody stopped
                    // working; counting its end would hand the next shift a rest
                    // it never got.
                    if (current.Gross > 0)
                        previousEnd = Math.Max(previousEnd, current.End);
                }

                live.RemoveAll(b => b.Gross <= 0);
                if (!moved || live.Count == 0)
                    break;
            }

            // Whatever the sweeps could not settle cannot be run: dropping it is
            // the honest answer, and it only ever lengthens the rest around it -
            // so one look at the settled ring is enough. The list is read whole
            // before anything leaves it; RemoveAll would renumber it underfoot.
            live.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            var doomed = live.Where(b => GapBefore(b, live) < rest).ToList();
            foreach (var block in doomed)
            {
                if (firstGap.TryAdd(block, GapBefore(block, live)))
                    shortened.Add(block);

                block.Start = block.End;
            }

            live.RemoveAll(doomed.Contains);

            foreach (var block in shortened.OrderBy(b => originalStart[b]))
            {
                applications.Add(new RuleApplication(WorkingTimeRuleId.RestPeriod, block.Shift.Key, block.Day,
                    TimeSpan.FromSeconds(firstGap[block]), TimeSpan.FromSeconds(rest)));

                foreach (var (start, end) in Normalise(originalStart[block], Math.Min(block.Start, block.End)))
                    annotations.Add(new WeekWindow(start, end, SegmentKind.Rest, block.Shift.Key, block.Day));
            }

            ReportRestCompensation(live, rules, applications);
            kept.AddRange(live);
        }

        return kept.OrderBy(b => b.Start).ToList();
    }

    /// <summary>
    /// The rest <paramref name="block"/> gets after the crew's previous shift,
    /// in <paramref name="ordered"/> — sorted by start, one crew, one week. The
    /// first shift of the week is measured against the last one of the one
    /// before, because the pattern repeats.
    /// </summary>
    private static long GapBefore(Block block, List<Block> ordered)
    {
        int index = ordered.IndexOf(block);
        long previousEnd = index == 0
            ? ordered.Max(b => b.End) - Week
            : ordered.Take(index).Max(b => b.End);

        return block.Start - previousEnd;
    }

    /// <summary>
    /// §5 (2): the 10-hour rest is available only in the sectors the subsection
    /// names, and only where "jede Verkürzung der Ruhezeit … durch Verlängerung
    /// einer anderen Ruhezeit auf mindestens zwölf Stunden ausgeglichen wird". A
    /// plant that takes the shortening is told what it now owes, and one outside
    /// those sectors is told it has no such permission at all.
    /// </summary>
    private static void ReportRestCompensation(List<Block> ordered, WorkingTimeRules rules, List<RuleApplication> applications)
    {
        if (rules.MinimumRest >= TimeSpan.FromHours(11) || ordered.Count == 0)
            return;

        var crewShift = ordered[0].Shift;
        if (rules.RestExceptionSector == RestExceptionSector.None)
        {
            applications.Add(new RuleApplication(WorkingTimeRuleId.RestCompensation, crewShift.Key, ordered[0].Day,
                rules.MinimumRest, TimeSpan.FromHours(11)));
            return;
        }

        long full = 11 * Hour;
        long compensating = (long)rules.CompensatingRest.TotalSeconds;
        int shortened = 0;
        int compensated = 0;
        long latestEnd = ordered.Max(b => b.End);
        long runningEnd = long.MinValue;
        for (int i = 0; i < ordered.Count; i++)
        {
            long previousEnd = i == 0 ? latestEnd - Week : runningEnd;
            runningEnd = Math.Max(runningEnd, ordered[i].End);
            long gap = ordered[i].Start - previousEnd;
            if (gap < full)
                shortened++;
            else if (gap >= compensating)
                compensated++;
        }

        for (int i = compensated; i < shortened; i++)
            applications.Add(new RuleApplication(WorkingTimeRuleId.RestCompensation, crewShift.Key, ordered[0].Day,
                rules.MinimumRest, rules.CompensatingRest));
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
            long net = Net(block.Gross, rules);
            long total = (long)rules.BreakOwed(TimeSpan.FromSeconds(net)).TotalSeconds;

            // Presence the crew can neither work nor spend on a break is not
            // capacity; trimming it is what keeps net working time monotonic in
            // the length of the shift.
            if (net + total < block.Gross)
                block.End = block.Start + net + total;

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

    // ----- 6. windows, gaps and the per-crew view -----

    /// <summary>
    /// What each crew actually works in an ordinary week, before the windows of
    /// different crews are merged for the machine's benefit. §5, §3 per crew-day
    /// and §11 are all obligations owed to people, so they need the person's view,
    /// not the machine's.
    /// </summary>
    private static List<CrewShiftInstance> CrewShifts(List<Block> blocks, WorkingTimeRules rules)
    {
        long threshold = (long)rules.NightWorkThreshold.TotalSeconds;
        var instances = new List<CrewShiftInstance>();
        foreach (var block in blocks)
        {
            var spans = new List<WorkSpan>();
            long cursor = block.Start;
            foreach (var (breakStart, breakEnd) in block.Breaks.OrderBy(b => b.Start))
            {
                if (breakStart > cursor)
                    spans.Add(new WorkSpan(cursor, breakStart));
                cursor = breakEnd;
            }

            if (cursor < block.End)
                spans.Add(new WorkSpan(cursor, block.End));

            if (spans.Count == 0)
                continue;

            instances.Add(new CrewShiftInstance(
                block.Shift.Crew, block.Shift.Key, block.Day, block.Start, block.End, spans,
                NightSeconds(block, rules) > threshold));
        }

        instances.Sort((a, b) => a.StartSeconds != b.StartSeconds
            ? a.StartSeconds.CompareTo(b.StartSeconds)
            : string.CompareOrdinal(a.Crew, b.Crew));
        return instances;
    }

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
        // No early exit on an empty window list: a staffed pattern the rules
        // emptied has a closed week to describe, and describing it as "nothing at
        // all" is what made it look unconstrained.
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
                WeekWindow? best = null;
                int bestPriority = -1;
                foreach (var annotation in annotations)
                {
                    if (annotation.StartSeconds > start || annotation.EndSeconds < end)
                        continue;
                    int priority = Priority(annotation.Kind);
                    if (priority > bestPriority)
                    {
                        best = annotation;
                        bestPriority = priority;
                    }
                }

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
            var lookupFrom = DateOnly.FromDateTime(from.Date.AddDays(-1));
            if (lookupFrom.Year < GermanHolidays.MinYear)
                lookupFrom = new DateOnly(GermanHolidays.MinYear, 1, 1);

            foreach (var holiday in GermanHolidays.Between(
                         lookupFrom, DateOnly.FromDateTime(to.Date), rules.State, rules.IncludePartialHolidays))
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

        // Overlaps merge into one closed stretch. The earliest cause names the
        // kind; every distinct cause is kept, compared for equality rather than
        // by substring — "Wartung Halle 2" contains "Wartung" and "Halle", so a
        // substring test silently swallowed two of three causes, and it swallowed
        // every unlabelled one by construction.
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
                var causes = last.Causes.Contains(clipped.Label, StringComparer.Ordinal)
                    ? last.Causes
                    : [.. last.Causes, clipped.Label];
                merged[^1] = last with
                {
                    End = clipped.End > last.End ? clipped.End : last.End,
                    Label = string.Join("; ", causes),
                    Causes = causes
                };
            }
            else
            {
                merged.Add(clipped with { Causes = [clipped.Label] });
            }
        }

        return (merged, holidays);
    }

    /// <summary>
    /// §11 (3) gives a crew that works a Sunday a replacement rest day within two
    /// weeks, §11 (2) gives one for holiday work within eight weeks. A repeating
    /// weekly pattern cannot show where that day goes, but leaving the obligation
    /// unmentioned would let the permission look free of charge.
    /// </summary>
    private static void ReportReplacementRestDays(
        ShiftPattern pattern,
        WorkingTimeRules rules,
        List<CrewShiftInstance> crewShifts,
        DateTime from,
        DateTime to,
        List<RuleApplication> applications)
    {
        if (crewShifts.Count == 0)
            return;

        if (rules.SundayWorkAllowed)
        {
            long shift = (long)rules.SundayBoundaryShift.TotalSeconds;
            foreach (var crew in crewShifts
                         .Where(i => i.WorkingSpans.Any(s => s.StartSeconds < 7 * Day + shift && s.EndSeconds > 6 * Day + shift))
                         .Select(i => i.Crew)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(c => c, StringComparer.Ordinal))
            {
                applications.Add(new RuleApplication(WorkingTimeRuleId.ReplacementRestDay, crew, DayOfWeek.Sunday,
                    TimeSpan.Zero, rules.SundayReplacementRestWindow));
            }
        }

        if (!rules.HolidayWorkAllowed)
            return;

        // When holiday work is allowed the holidays are not exceptions, so they
        // are not in the timeline's list — but the obligation still needs one to
        // exist before it is worth reporting.
        var worked = GermanHolidays.Between(
            DateOnly.FromDateTime(from.Date), DateOnly.FromDateTime(to.Date.AddDays(-1)), rules.State, rules.IncludePartialHolidays);
        if (worked.Count > 0)
        {
            applications.Add(new RuleApplication(WorkingTimeRuleId.ReplacementRestDay, pattern.Key,
                worked[0].Date.DayOfWeek, TimeSpan.Zero, rules.HolidayReplacementRestWindow));
        }
    }

    /// <summary>The rules that can take working time away — what a closed week names as its cause.</summary>
    private static List<WorkingTimeRuleId> CuttingRules(List<RuleApplication> applications) =>
        applications
            .Select(a => a.Rule)
            .Where(r => r is WorkingTimeRuleId.SundayRest or WorkingTimeRuleId.HolidayRest
                or WorkingTimeRuleId.MaxDailyWorkingTime or WorkingTimeRuleId.NightWork or WorkingTimeRuleId.RestPeriod)
            .Distinct()
            .ToList();
}
