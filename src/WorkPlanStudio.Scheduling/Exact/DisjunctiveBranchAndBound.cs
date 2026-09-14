using System.Diagnostics;

namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// The search itself: a depth-first branch-and-bound over the disjunctive graph.
/// <para>
/// The routing arcs are fixed by the problem — step 20 follows step 10 — and the
/// only thing left to decide is the <i>order</i> in which the operations that
/// compete for a work center use it, plus which of its parallel slots each one
/// takes. A node extends the partial schedule by one operation: it appends an
/// operation whose routing predecessor is already sequenced to the end of one
/// slot's sequence, at the earliest moment the calendar, the change-over and the
/// two predecessors allow.
/// </para>
/// <para>
/// Why that is exact, and not merely thorough. Every feasible schedule induces an
/// order per slot; the schedule that starts every operation as early as that
/// order permits has completion times no later than the original, because a
/// placement's end is monotone in the moment it may start (see
/// <see cref="CalendarPlacement"/>); and the objective — a non-negative weighted
/// sum of makespan, total tardiness and late jobs — is non-decreasing in
/// completion times. So the best schedule for an order is the one this search
/// builds, and the search reaches every order. A change-over depends on the
/// order and not on the timing, so it survives that argument untouched.
/// </para>
/// <para>
/// What keeps it finite: the relaxation bound of <see cref="RelaxationBounds"/>
/// against the incumbent, the propagation of <see cref="DisjunctivePropagator"/>,
/// the <see cref="SearchStateMemo"/> against re-derivation, and a symmetry break
/// that offers only one of a work center's idle slots — they are identical, and
/// treating them as distinct multiplies the tree by their factorial for nothing.
/// </para>
/// </summary>
internal sealed class DisjunctiveBranchAndBound
{
    private const double Tolerance = 1e-9;

    private readonly ExactInstance _instance;
    private readonly ExactSolverOptions _options;
    private readonly ExactSearchState _state;
    private readonly RelaxationBounds _bounds;
    private readonly DisjunctivePropagator? _propagator;
    private readonly SearchStateMemo? _memo;
    private readonly Stopwatch _clock = new();

    private readonly long[] _jobDeadline;
    private readonly Extension[] _extensions;
    private readonly int _extensionStride;

    private readonly long[] _bestStart;
    private readonly long[] _bestEnd;
    private readonly long[] _bestSetup;
    private readonly int[] _bestSlot;

    private CancellationToken _cancellation;
    private double _incumbent = double.PositiveInfinity;
    private bool _hasIncumbent;
    private double _rootBound;
    private double _abandonedBound = double.PositiveInfinity;
    private long _nodes;
    private long _pruned;
    private bool _limitReached;

    internal DisjunctiveBranchAndBound(ExactInstance instance, ExactSolverOptions options)
    {
        _instance = instance;
        _options = options;
        _state = new ExactSearchState(instance);
        _bounds = new RelaxationBounds(instance);
        _propagator = options.UseEdgeFinding ? new DisjunctivePropagator(instance, _bounds) : null;
        _memo = options.UseStateMemo && options.StateMemoCapacity > 0
            ? new SearchStateMemo(instance, options.StateMemoCapacity)
            : null;

        _jobDeadline = new long[instance.JobCount];

        int widestWorkCenter = 1;
        for (int machine = 0; machine < instance.MachineCount; machine++)
            widestWorkCenter = Math.Max(widestWorkCenter, instance.Capacity(machine));

        _extensionStride = Math.Max(1, instance.JobCount * widestWorkCenter);
        _extensions = new Extension[Math.Max(1, instance.OperationCount) * _extensionStride];

        _bestStart = new long[instance.OperationCount];
        _bestEnd = new long[instance.OperationCount];
        _bestSetup = new long[instance.OperationCount];
        _bestSlot = new int[instance.OperationCount];
    }

    internal ExactSolution Solve(CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;
        _state.Reset(_instance);
        _clock.Restart();

        if (_instance.OperationCount == 0)
        {
            _clock.Stop();
            return new ExactSolution(
                ExactSolutionStatus.Optimal, new Schedule([], []), 0, 0, 0, 0, 0, _clock.Elapsed);
        }

        _bounds.ComputeHeads(_state);
        _bounds.ComputeObjective(_state);
        _rootBound = _bounds.PenaltyLowerBound;

        Visit();
        _clock.Stop();

        if (!_hasIncumbent)
        {
            return new ExactSolution(
                ExactSolutionStatus.NoSolutionFound, null, null, _rootBound, 0, _nodes, _pruned, _clock.Elapsed);
        }

        double bound = _limitReached
            ? Math.Max(_rootBound, Math.Min(_incumbent, _abandonedBound))
            : _incumbent;

        var status = bound >= _incumbent - Tolerance
            ? ExactSolutionStatus.Optimal
            : ExactSolutionStatus.FeasibleWithGap;

        var schedule = BuildSchedule();
        return new ExactSolution(
            status, schedule, _incumbent, bound, schedule.MakespanSeconds, _nodes, _pruned, _clock.Elapsed);
    }

    private void Visit()
    {
        _cancellation.ThrowIfCancellationRequested();
        _nodes++;

        if (_state.PlacedCount == _instance.OperationCount)
        {
            RecordIncumbent();
            return;
        }

        _bounds.ComputeHeads(_state);
        _bounds.ComputeObjective(_state);
        double lowerBound = _bounds.PenaltyLowerBound;
        if (_hasIncumbent && lowerBound >= _incumbent - Tolerance)
        {
            _pruned++;
            return;
        }

        // Propagation needs a deadline to push against, and the incumbent is where
        // the deadline comes from. Before the first leaf there is nothing to
        // propagate towards, so it is skipped rather than run for no deductions.
        if (_propagator is not null && _hasIncumbent)
        {
            ComputeDeadlines(lowerBound);
            if (!_propagator.Propagate(_state, _bounds.EarliestStart, _jobDeadline, maxRounds: 3))
            {
                _pruned++;
                return;
            }

            _bounds.ComputeObjective(_state);
            lowerBound = _bounds.PenaltyLowerBound;
            if (lowerBound >= _incumbent - Tolerance)
            {
                _pruned++;
                return;
            }
        }

        if (_memo is not null && !_memo.TryAdd(_state))
        {
            _pruned++;
            return;
        }

        int offset = _state.PlacedCount * _extensionStride;
        int count = BuildExtensions(offset);
        SortByEarliestFinish(offset, count);

        for (int i = 0; i < count; i++)
        {
            if (LimitReached())
            {
                _limitReached = true;
                _abandonedBound = Math.Min(_abandonedBound, lowerBound);
                return;
            }

            var extension = _extensions[offset + i];
            int operation = extension.Operation;
            int slot = extension.Slot;
            int job = _instance.JobOfOperation[operation];

            long previousSlotFreeAt = _state.SlotFreeAt[slot];
            int previousSlotFamily = _state.SlotFamily[slot];
            int previousSlotLast = _state.SlotLastOperation[slot];
            long previousJobReadyAt = _state.JobReadyAt[job];
            long previousMakespan = _state.PartialMakespan;

            _state.OperationStart[operation] = extension.Start;
            _state.OperationEnd[operation] = extension.End;
            _state.OperationSetup[operation] = extension.Setup;
            _state.OperationSlot[operation] = slot;
            _state.SlotFreeAt[slot] = extension.End;
            _state.SlotFamily[slot] = _instance.FamilyOfOperation[operation];
            _state.SlotLastOperation[slot] = operation;
            _state.SlotOperationCount[slot]++;
            _state.JobReadyAt[job] = extension.End;
            _state.JobPlaced[job]++;
            _state.PlacedCount++;
            _state.PartialMakespan = Math.Max(previousMakespan, extension.End);

            Visit();

            _state.PartialMakespan = previousMakespan;
            _state.PlacedCount--;
            _state.JobPlaced[job]--;
            _state.JobReadyAt[job] = previousJobReadyAt;
            _state.SlotOperationCount[slot]--;
            _state.SlotLastOperation[slot] = previousSlotLast;
            _state.SlotFamily[slot] = previousSlotFamily;
            _state.SlotFreeAt[slot] = previousSlotFreeAt;

            if (_limitReached)
            {
                _abandonedBound = Math.Min(_abandonedBound, lowerBound);
                return;
            }
        }
    }

    private bool LimitReached() =>
        _nodes >= _options.NodeLimit ||
        (_options.TimeLimit is { } limit && _clock.Elapsed >= limit);

    /// <summary>
    /// Every way the partial schedule can grow by one operation: each job's next
    /// unsequenced operation, on each slot of its work center that is not a
    /// duplicate of one already offered.
    /// </summary>
    private int BuildExtensions(int offset)
    {
        var instance = _instance;
        int count = 0;

        for (int job = 0; job < instance.JobCount; job++)
        {
            int operation = instance.JobFirst[job] + _state.JobPlaced[job];
            if (operation >= instance.JobFirst[job + 1])
                continue;

            int machine = instance.MachineOfOperation[operation];
            int family = instance.FamilyOfOperation[operation];
            long duration = instance.Duration[operation];
            long ready = _state.JobReadyAt[job];
            bool idleSlotOffered = false;

            for (int slot = instance.SlotStart(machine); slot < instance.SlotEnd(machine); slot++)
            {
                // Idle slots of one work center are interchangeable: same clock,
                // same (absent) change-over history. Offering more than one of them
                // multiplies the tree by their permutations and finds nothing.
                if (_state.SlotOperationCount[slot] == 0)
                {
                    if (idleSlotOffered)
                        continue;
                    idleSlotOffered = true;
                }

                long setup = instance.Setup(machine, _state.SlotFamily[slot], family);
                long earliest = Math.Max(ready, _state.SlotFreeAt[slot]);
                var (start, end) = CalendarPlacement.Earliest(instance.Machines[machine], earliest, setup + duration);
                _extensions[offset + count++] = new Extension(operation, slot, start, end, setup);
            }
        }

        return count;
    }

    /// <summary>
    /// Orders the children so the search dives towards a good schedule first — an
    /// incumbent found early is what makes the bound prune anything at all.
    /// Insertion sort: the lists are a handful of entries and it allocates nothing.
    /// </summary>
    private void SortByEarliestFinish(int offset, int count)
    {
        for (int i = 1; i < count; i++)
        {
            var candidate = _extensions[offset + i];
            int j = i - 1;
            while (j >= 0 && IsAfter(_extensions[offset + j], candidate))
            {
                _extensions[offset + j + 1] = _extensions[offset + j];
                j--;
            }

            _extensions[offset + j + 1] = candidate;
        }
    }

    private static bool IsAfter(in Extension left, in Extension right)
    {
        if (left.End != right.End) return left.End > right.End;
        if (left.Start != right.Start) return left.Start > right.Start;
        if (left.Operation != right.Operation) return left.Operation > right.Operation;
        return left.Slot > right.Slot;
    }

    /// <summary>
    /// The latest completion each job may still have if this subtree is to beat the
    /// incumbent, derived from how much objective is left unspent.
    /// </summary>
    private void ComputeDeadlines(double lowerBound)
    {
        var parameters = _instance.Parameters;
        double residual = _incumbent - lowerBound;

        long makespanDeadline = parameters.MakespanWeight > 0
            ? Add(_bounds.MakespanLowerBound, Seconds(residual / parameters.MakespanWeight))
            : DisjunctivePropagator.NoDeadline;

        for (int job = 0; job < _instance.JobCount; job++)
        {
            long deadline = makespanDeadline;

            if (parameters.TardinessWeight > 0)
            {
                long baseline = Math.Max(_instance.Due[job], _bounds.JobCompletion[job]);
                deadline = Math.Min(deadline, Add(baseline, Seconds(residual / parameters.TardinessWeight)));
            }

            // A job that is not yet forced to be late cannot afford to become one
            // when the flat late penalty alone would exhaust what is left.
            if (parameters.LatePenalty >= residual && _bounds.JobCompletion[job] <= _instance.Due[job])
                deadline = Math.Min(deadline, _instance.Due[job]);

            _jobDeadline[job] = deadline;
        }
    }

    private static long Seconds(double hours)
    {
        double seconds = Math.Floor(hours * 3600.0);
        return seconds >= DisjunctivePropagator.NoDeadline ? DisjunctivePropagator.NoDeadline : (long)Math.Max(0, seconds);
    }

    private static long Add(long value, long offset)
    {
        long sum = value + offset;
        return sum < 0 || sum > DisjunctivePropagator.NoDeadline ? DisjunctivePropagator.NoDeadline : sum;
    }

    private void RecordIncumbent()
    {
        var instance = _instance;
        long makespan = 0;
        long tardiness = 0;
        int late = 0;

        for (int job = 0; job < instance.JobCount; job++)
        {
            long completion = _state.JobReadyAt[job];
            if (completion > makespan)
                makespan = completion;

            long over = completion - instance.Due[job];
            if (over > 0)
            {
                tardiness += over;
                late++;
            }
        }

        var parameters = instance.Parameters;
        double penalty =
            parameters.MakespanWeight * (makespan / 3600.0) +
            parameters.TardinessWeight * (tardiness / 3600.0) +
            parameters.LatePenalty * late;

        if (_hasIncumbent && penalty >= _incumbent - Tolerance)
            return;

        _incumbent = penalty;
        _hasIncumbent = true;
        Array.Copy(_state.OperationStart, _bestStart, instance.OperationCount);
        Array.Copy(_state.OperationEnd, _bestEnd, instance.OperationCount);
        Array.Copy(_state.OperationSetup, _bestSetup, instance.OperationCount);
        Array.Copy(_state.OperationSlot, _bestSlot, instance.OperationCount);
    }

    private Schedule BuildSchedule()
    {
        var instance = _instance;
        var operations = new ScheduledOperation[instance.OperationCount];
        for (int operation = 0; operation < instance.OperationCount; operation++)
        {
            int job = instance.JobOfOperation[operation];
            int machine = instance.MachineOfOperation[operation];
            long paused = _bestEnd[operation] - _bestStart[operation] - _bestSetup[operation] - instance.Duration[operation];

            operations[operation] = new ScheduledOperation(
                instance.Jobs[job].Id,
                instance.StepNumbers[operation],
                instance.WorkCenterIds[machine],
                _bestSlot[operation] - instance.SlotStart(machine),
                _bestStart[operation],
                _bestEnd[operation],
                _bestSetup[operation],
                paused);
        }

        var rows = new JobSchedule[instance.JobCount];
        for (int job = 0; job < instance.JobCount; job++)
        {
            var definition = instance.Jobs[job];
            rows[job] = new JobSchedule(
                definition.Id,
                definition.Reference,
                definition.ReleaseSeconds,
                instance.Due[job],
                _bestEnd[instance.JobFirst[job + 1] - 1]);
        }

        return new Schedule(operations, rows);
    }

    /// <summary>One way of growing the partial schedule, and where that operation would land.</summary>
    private readonly record struct Extension(int Operation, int Slot, long Start, long End, long Setup);
}
