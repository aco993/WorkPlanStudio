namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// Constraint propagation on the disjunctive graph: tightens the time window of
/// every operation the search has not sequenced yet, and proves some nodes
/// infeasible before the bound would have got round to it.
/// <para>
/// Three layers, cheapest first.
/// <list type="number">
/// <item><b>Time-window tightening</b> — a routing is a chain, so an earliest
/// start pushes everything behind it and a deadline pulls everything in front of
/// it. One forward and one backward sweep per job.</item>
/// <item><b>Overload</b> — if the operations that must run inside a window need
/// more machine-seconds than the window has, the node is dead. This is the one
/// rule that also applies to a work center with several parallel slots, divided
/// by the slot count.</item>
/// <item><b>Edge finding, not-first and not-last</b> — on a single-slot work
/// center, if a set of operations plus one more cannot fit between the set's
/// earliest start and its latest completion, that one operation must run after
/// the whole set (or before it, or at least not first, or at least not last).
/// Each conclusion is a tighter bound, and a tighter bound is a smaller
/// tree.</item>
/// </list>
/// </para>
/// <para>
/// Every rule is stated in processing time and ignores change-over and calendars,
/// which makes it a relaxation of the real constraint. That is the right
/// direction: a relaxation can only fail to deduce something, never deduce
/// something false.
/// </para>
/// </summary>
internal sealed class DisjunctivePropagator
{
    /// <summary>A deadline no schedule can reach, used where the objective implies none.</summary>
    internal const long NoDeadline = long.MaxValue / 4;

    private readonly ExactInstance _instance;
    private readonly RelaxationBounds _bounds;
    private readonly long[] _latestCompletion;
    private readonly int[] _members;
    private readonly long[] _memberEarliest;
    private readonly long[] _memberLatest;
    private readonly long[] _memberDuration;
    private readonly bool[] _inWindow;

    internal DisjunctivePropagator(ExactInstance instance, RelaxationBounds bounds)
    {
        _instance = instance;
        _bounds = bounds;
        _latestCompletion = new long[instance.OperationCount];
        _members = new int[instance.OperationCount];
        _memberEarliest = new long[instance.OperationCount];
        _memberLatest = new long[instance.OperationCount];
        _memberDuration = new long[instance.OperationCount];
        _inWindow = new bool[instance.OperationCount];
    }

    /// <summary>
    /// Runs the three layers to a fixpoint (or <paramref name="maxRounds"/>
    /// rounds) over the operations the search has not sequenced.
    /// </summary>
    /// <param name="state">Where the search has got to.</param>
    /// <param name="earliestStart">Heads, tightened in place.</param>
    /// <param name="jobDeadline">Latest completion each job may still have; <see cref="NoDeadline"/> for none.</param>
    /// <param name="maxRounds">How many times to re-run the layers after something changed.</param>
    /// <returns><c>false</c> when the node is proved to hold no schedule worth having.</returns>
    internal bool Propagate(ExactSearchState state, long[] earliestStart, long[] jobDeadline, int maxRounds)
    {
        var instance = _instance;

        for (int job = 0; job < instance.JobCount; job++)
        {
            long limit = jobDeadline[job];
            for (int operation = instance.JobFirst[job + 1] - 1; operation >= instance.JobFirst[job]; operation--)
            {
                _latestCompletion[operation] = limit;
                limit -= instance.Duration[operation];
            }
        }

        for (int round = 0; round < maxRounds; round++)
        {
            bool changed = false;

            for (int job = 0; job < instance.JobCount; job++)
            {
                int next = instance.JobFirst[job] + state.JobPlaced[job];
                int last = instance.JobFirst[job + 1];

                long ready = state.JobReadyAt[job];
                for (int operation = next; operation < last; operation++)
                {
                    if (ready > earliestStart[operation])
                    {
                        earliestStart[operation] = ready;
                        changed = true;
                    }

                    ready = earliestStart[operation] + instance.Duration[operation];
                }

                long limit = jobDeadline[job];
                for (int operation = last - 1; operation >= next; operation--)
                {
                    if (limit < _latestCompletion[operation])
                    {
                        _latestCompletion[operation] = limit;
                        changed = true;
                    }

                    limit = _latestCompletion[operation] - instance.Duration[operation];
                }

                for (int operation = next; operation < last; operation++)
                {
                    if (earliestStart[operation] + instance.Duration[operation] > _latestCompletion[operation])
                        return false;
                }
            }

            for (int machine = 0; machine < instance.MachineCount; machine++)
            {
                int count = Collect(state, machine, earliestStart);
                if (count < 2)
                    continue;

                if (!Tighten(machine, count, ref changed))
                    return false;

                Publish(count, earliestStart);
            }

            if (!changed)
                break;
        }

        return true;
    }

    /// <summary>Copies the unsequenced operations of one work center into the scratch arrays.</summary>
    private int Collect(ExactSearchState state, int machineIndex, long[] earliestStart)
    {
        var instance = _instance;
        int count = 0;
        foreach (int operation in _bounds.OperationsOn(machineIndex))
        {
            int job = instance.JobOfOperation[operation];
            if (operation < instance.JobFirst[job] + state.JobPlaced[job])
                continue;

            _members[count] = operation;
            _memberEarliest[count] = earliestStart[operation];
            _memberLatest[count] = _latestCompletion[operation];
            _memberDuration[count] = instance.Duration[operation];
            count++;
        }

        return count;
    }

    private void Publish(int count, long[] earliestStart)
    {
        for (int i = 0; i < count; i++)
        {
            earliestStart[_members[i]] = _memberEarliest[i];
            _latestCompletion[_members[i]] = _memberLatest[i];
        }
    }

    private bool Tighten(int machineIndex, int count, ref bool changed)
    {
        int capacity = _instance.Capacity(machineIndex);

        // Every candidate window is bounded by some operation's earliest start and
        // some operation's latest completion — the classic task-interval
        // enumeration, which is enough to make all four rules complete.
        for (int a = 0; a < count; a++)
        {
            for (int b = 0; b < count; b++)
            {
                long windowStart = _memberEarliest[a];
                long windowEnd = _memberLatest[b];
                if (windowEnd <= windowStart)
                    continue;

                long work = 0;
                long setEarliest = long.MaxValue;
                long setLatest = long.MinValue;
                long earliestRelease = long.MaxValue;
                long latestDeadline = long.MinValue;
                int size = 0;
                for (int i = 0; i < count; i++)
                {
                    bool inside = _memberEarliest[i] >= windowStart && _memberLatest[i] <= windowEnd;
                    _inWindow[i] = inside;
                    if (!inside)
                        continue;

                    size++;
                    work += _memberDuration[i];
                    setEarliest = Math.Min(setEarliest, _memberEarliest[i]);
                    setLatest = Math.Max(setLatest, _memberLatest[i]);
                    earliestRelease = Math.Min(earliestRelease, _memberEarliest[i] + _memberDuration[i]);
                    latestDeadline = Math.Max(latestDeadline, _memberLatest[i] - _memberDuration[i]);
                }

                if (size == 0)
                    continue;

                // Overload: the set needs more machine-seconds than the window holds.
                long available = capacity == 1 ? work : (work + capacity - 1) / capacity;
                if (setEarliest + available > setLatest)
                    return false;

                if (capacity > 1)
                    continue;   // the rules below are unary-resource rules

                for (int i = 0; i < count; i++)
                {
                    if (_inWindow[i])
                        continue;

                    long duration = _memberDuration[i];

                    // Edge finding, set ≪ i: the set plus i does not fit before the
                    // set's deadline, so i runs after all of it.
                    if (Math.Min(setEarliest, _memberEarliest[i]) + work + duration > setLatest &&
                        _memberEarliest[i] < setEarliest + work)
                    {
                        _memberEarliest[i] = setEarliest + work;
                        changed = true;
                    }

                    // Edge finding, i ≪ set: symmetric, so i runs before all of it.
                    if (setEarliest + work + duration > Math.Max(setLatest, _memberLatest[i]) &&
                        _memberLatest[i] > setLatest - work)
                    {
                        _memberLatest[i] = setLatest - work;
                        changed = true;
                    }

                    // Not first: i cannot precede the whole set.
                    if (_memberEarliest[i] + duration + work > setLatest && _memberEarliest[i] < earliestRelease)
                    {
                        _memberEarliest[i] = earliestRelease;
                        changed = true;
                    }

                    // Not last: i cannot follow the whole set.
                    if (setEarliest + work + duration > _memberLatest[i] && _memberLatest[i] > latestDeadline)
                    {
                        _memberLatest[i] = latestDeadline;
                        changed = true;
                    }

                    if (_memberEarliest[i] + duration > _memberLatest[i])
                        return false;
                }
            }
        }

        return true;
    }
}
