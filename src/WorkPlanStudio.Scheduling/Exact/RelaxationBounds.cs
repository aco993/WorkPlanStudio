namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// The lower bounds the branch-and-bound prunes with: how early every remaining
/// operation can possibly start, how early every job can possibly finish, and
/// what that implies for the objective the engine actually minimises.
/// <para>
/// Two families of bound, because they fail in different places. The
/// <b>job bound</b> follows each routing forward and is tight when a job is long
/// and the shop is empty. The <b>machine bound</b> asks what one work center can
/// physically get through and is tight when a work center is the bottleneck — on
/// a single-slot work center it is Jackson's preemptive schedule, which is the
/// strongest bound obtainable in polynomial time from heads and tails alone
/// (relax the machine to allow preemption, schedule by longest tail, and read off
/// the makespan).
/// </para>
/// <para>
/// Both bounds ignore change-over and calendars. That is not an oversight: a
/// change-over only adds time and a calendar only pushes work later, so dropping
/// them gives a relaxation, and a relaxation is what a lower bound has to be. It
/// does mean the bound is weaker on an instance with a heavy setup matrix, which
/// is visible in the node counts of the optimality study.
/// </para>
/// </summary>
internal sealed class RelaxationBounds
{
    private readonly ExactInstance _instance;
    private readonly int[][] _operationsByMachine;
    private readonly long[] _machineReadyAt;

    // Jackson's preemptive schedule works on a copy of the machine's operations.
    private readonly long[] _jacksonRemaining;
    private readonly int[] _jacksonMembers;

    /// <summary>Earliest start of each operation under the relaxation; only the unscheduled entries are meaningful.</summary>
    internal long[] EarliestStart { get; }

    /// <summary>Earliest completion of each job under the relaxation.</summary>
    internal long[] JobCompletion { get; }

    internal long MakespanLowerBound { get; private set; }
    internal long TardinessLowerBound { get; private set; }
    internal int LateJobLowerBound { get; private set; }
    internal double PenaltyLowerBound { get; private set; }

    internal RelaxationBounds(ExactInstance instance)
    {
        _instance = instance;
        _machineReadyAt = new long[instance.MachineCount];
        EarliestStart = new long[instance.OperationCount];
        JobCompletion = new long[instance.JobCount];
        _jacksonRemaining = new long[instance.OperationCount];
        _jacksonMembers = new int[instance.OperationCount];

        var counts = new int[instance.MachineCount];
        for (int operation = 0; operation < instance.OperationCount; operation++)
            counts[instance.MachineOfOperation[operation]]++;

        _operationsByMachine = new int[instance.MachineCount][];
        for (int machine = 0; machine < instance.MachineCount; machine++)
            _operationsByMachine[machine] = new int[counts[machine]];

        var cursor = new int[instance.MachineCount];
        for (int operation = 0; operation < instance.OperationCount; operation++)
        {
            int machine = instance.MachineOfOperation[operation];
            _operationsByMachine[machine][cursor[machine]++] = operation;
        }
    }

    /// <summary>Operations of one work center, in routing order — the machine bound's input set.</summary>
    internal int[] OperationsOn(int machineIndex) => _operationsByMachine[machineIndex];

    /// <summary>
    /// Fills <see cref="EarliestStart"/> by walking each routing forward from where
    /// the search has left it, respecting the earliest moment the operation's work
    /// center can take anything at all.
    /// </summary>
    internal void ComputeHeads(ExactSearchState state)
    {
        var instance = _instance;
        for (int machine = 0; machine < instance.MachineCount; machine++)
        {
            long ready = long.MaxValue;
            for (int slot = instance.SlotStart(machine); slot < instance.SlotEnd(machine); slot++)
                ready = Math.Min(ready, state.SlotFreeAt[slot]);
            _machineReadyAt[machine] = ready;
        }

        for (int job = 0; job < instance.JobCount; job++)
        {
            long ready = state.JobReadyAt[job];
            int next = instance.JobFirst[job] + state.JobPlaced[job];
            int last = instance.JobFirst[job + 1];
            for (int operation = next; operation < last; operation++)
            {
                long earliest = Math.Max(ready, _machineReadyAt[instance.MachineOfOperation[operation]]);
                EarliestStart[operation] = earliest;
                ready = earliest + instance.Duration[operation];
            }

            JobCompletion[job] = ready;
        }
    }

    /// <summary>
    /// Rolls <see cref="EarliestStart"/> up into the three objective terms and the
    /// penalty. Call after <see cref="ComputeHeads"/>, and again after propagation
    /// has tightened the heads.
    /// </summary>
    internal void ComputeObjective(ExactSearchState state)
    {
        var instance = _instance;

        // Re-derive the job completions from the (possibly tightened) heads: the
        // longest chain through the remaining operations of the job, not merely
        // the sum of what is left.
        for (int job = 0; job < instance.JobCount; job++)
        {
            long completion = state.JobReadyAt[job];
            int next = instance.JobFirst[job] + state.JobPlaced[job];
            int last = instance.JobFirst[job + 1];
            for (int operation = next; operation < last; operation++)
            {
                long finish = EarliestStart[operation] + instance.Duration[operation] + instance.TailWork[operation];
                if (finish > completion)
                    completion = finish;
            }

            JobCompletion[job] = completion;
        }

        long makespan = state.PartialMakespan;
        for (int job = 0; job < instance.JobCount; job++)
            makespan = Math.Max(makespan, JobCompletion[job]);

        for (int machine = 0; machine < instance.MachineCount; machine++)
            makespan = Math.Max(makespan, MachineBound(state, machine));

        long tardiness = 0;
        int late = 0;
        for (int job = 0; job < instance.JobCount; job++)
        {
            long over = JobCompletion[job] - instance.Due[job];
            if (over > 0)
            {
                tardiness += over;
                late++;
            }
        }

        MakespanLowerBound = makespan;
        TardinessLowerBound = tardiness;
        LateJobLowerBound = late;

        var parameters = instance.Parameters;
        PenaltyLowerBound =
            parameters.MakespanWeight * (makespan / 3600.0) +
            parameters.TardinessWeight * (tardiness / 3600.0) +
            parameters.LatePenalty * late;
    }

    /// <summary>
    /// What one work center still has to get through, plus the shortest tail behind
    /// it. Jackson's preemptive schedule for a single slot, a volume argument for
    /// several.
    /// </summary>
    private long MachineBound(ExactSearchState state, int machineIndex)
    {
        var instance = _instance;
        var candidates = _operationsByMachine[machineIndex];

        int count = 0;
        long totalWork = 0;
        long earliest = long.MaxValue;
        long shortestTail = long.MaxValue;
        foreach (int operation in candidates)
        {
            int job = instance.JobOfOperation[operation];
            if (operation < instance.JobFirst[job] + state.JobPlaced[job])
                continue;   // already sequenced

            _jacksonMembers[count] = operation;
            _jacksonRemaining[count] = instance.Duration[operation];
            count++;
            totalWork += instance.Duration[operation];
            earliest = Math.Min(earliest, EarliestStart[operation]);
            shortestTail = Math.Min(shortestTail, instance.TailWork[operation]);
        }

        if (count == 0)
            return 0;

        int capacity = instance.Capacity(machineIndex);
        if (capacity > 1)
            return earliest + (totalWork + capacity - 1) / capacity + shortestTail;

        return JacksonPreemptiveBound(count);
    }

    /// <summary>
    /// The makespan of the preemptive single-machine schedule that always runs the
    /// released operation with the longest tail, measured as
    /// <c>max(completion + tail)</c>. A valid lower bound because preemption can
    /// only help and every tail still has to run after its operation.
    /// </summary>
    private long JacksonPreemptiveBound(int count)
    {
        var instance = _instance;

        long clock = long.MaxValue;
        long bound = 0;
        int remainingCount = 0;
        for (int i = 0; i < count; i++)
        {
            int operation = _jacksonMembers[i];
            clock = Math.Min(clock, EarliestStart[operation]);

            // A zero-length operation occupies no machine time, so it takes no
            // part in the preemptive simulation — but its own start plus tail is
            // still a bound, and counting it as "remaining" would leave the
            // simulation with nothing to run and no way to finish.
            if (_jacksonRemaining[i] > 0)
                remainingCount++;
            else
                bound = Math.Max(bound, EarliestStart[operation] + instance.TailWork[operation]);
        }

        while (remainingCount > 0)
        {
            int chosen = -1;
            long chosenTail = -1;
            long nextRelease = long.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (_jacksonRemaining[i] <= 0)
                    continue;

                int operation = _jacksonMembers[i];
                long release = EarliestStart[operation];
                if (release > clock)
                {
                    nextRelease = Math.Min(nextRelease, release);
                    continue;
                }

                long tail = instance.TailWork[operation];
                if (tail > chosenTail)
                {
                    chosenTail = tail;
                    chosen = i;
                }
            }

            if (chosen < 0)
            {
                if (nextRelease == long.MaxValue)
                    break;   // nothing left that can run: defensive, not reachable

                clock = nextRelease;   // idle until something is released
                continue;
            }

            // Run until the chosen operation finishes, or until an operation with a
            // strictly longer tail arrives and preempts it.
            long preemptAt = long.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (i == chosen || _jacksonRemaining[i] <= 0)
                    continue;

                int operation = _jacksonMembers[i];
                long release = EarliestStart[operation];
                if (release > clock && instance.TailWork[operation] > chosenTail)
                    preemptAt = Math.Min(preemptAt, release);
            }

            long finish = clock + _jacksonRemaining[chosen];
            if (finish <= preemptAt)
            {
                _jacksonRemaining[chosen] = 0;
                clock = finish;
                bound = Math.Max(bound, clock + chosenTail);
                remainingCount--;
            }
            else
            {
                _jacksonRemaining[chosen] -= preemptAt - clock;
                clock = preemptAt;
            }
        }

        return bound;
    }
}
