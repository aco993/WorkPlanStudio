namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// The instance in the shape a search over machine sequences needs: operations
/// numbered 0..n-1 in routing order, each with its job, its work center, its
/// duration and its change-over family, plus the parallel slots the work centers
/// offer and the target date each job is measured against.
/// <para>
/// This is a projection of <see cref="SchedulingContext"/>, not a second model of
/// it. The exact solver shares the <i>problem</i> with the heuristic — the same
/// durations, the same calendars, the same change-over matrix, the same target
/// dates from <see cref="DueDateAssigner"/> — and shares none of the machinery
/// that turns a problem into a schedule. That is what makes it usable as an
/// oracle: a modelling bug in the dispatcher cannot cancel out against the same
/// bug in the reference.
/// </para>
/// </summary>
internal sealed class ExactInstance
{
    private readonly SchedulingContext _context;
    private readonly int[] _slotOffsetByMachine;

    internal int JobCount { get; }
    internal int OperationCount { get; }
    internal int MachineCount { get; }
    internal int SlotCount { get; }

    /// <summary>First operation index of each job; length <see cref="JobCount"/> + 1, so job j owns <c>[JobFirst[j], JobFirst[j+1])</c>.</summary>
    internal int[] JobFirst { get; }
    internal int[] JobOfOperation { get; }
    internal int[] MachineOfOperation { get; }
    internal int[] FamilyOfOperation { get; }
    internal long[] Duration { get; }

    /// <summary>Sum of the durations of the operations that follow this one in its job — the classic "tail".</summary>
    internal long[] TailWork { get; }

    internal long[] Release { get; }
    internal long[] Due { get; }

    internal MachineCapacity[] Machines { get; }
    internal int[] WorkCenterIds { get; }
    internal int[] StepNumbers { get; }

    /// <summary>True when at least one work center declares a change-over matrix.</summary>
    internal bool HasSetups { get; }

    /// <summary>True when at least one work center declares availability windows or blackouts.</summary>
    internal bool HasCalendars { get; }

    /// <summary>
    /// An upper bound on any completion time in this instance, ignoring calendars:
    /// the latest release plus every operation plus the worst change-over into
    /// each operation. Used as the big-M of the MILP model, and as the fallback
    /// deadline of the propagator.
    /// </summary>
    internal long Horizon { get; }

    internal SchedulingParameters Parameters => _context.Parameters;
    internal IReadOnlyList<ProductionJob> Jobs => _context.Jobs;

    internal ExactInstance(SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob)
    {
        _context = context;

        JobCount = context.Jobs.Count;
        OperationCount = context.TotalSteps;
        MachineCount = context.MachineIdsSorted.Length;
        SlotCount = context.TotalSlots;

        _slotOffsetByMachine = context.SlotOffsetByMachineIndex;
        Machines = context.MachinesByIndex;
        WorkCenterIds = context.MachineIdsSorted;
        StepNumbers = context.StepNumbers;

        JobFirst = context.JobStepOffset;
        MachineOfOperation = context.StepMachineIndex;
        FamilyOfOperation = context.StepFamilyId;
        Duration = context.StepDuration;
        Release = context.JobRelease;

        JobOfOperation = new int[OperationCount];
        TailWork = new long[OperationCount];
        for (int job = 0; job < JobCount; job++)
        {
            long tail = 0;
            for (int operation = JobFirst[job + 1] - 1; operation >= JobFirst[job]; operation--)
            {
                JobOfOperation[operation] = job;
                TailWork[operation] = tail;
                tail += Duration[operation];
            }
        }

        Due = new long[JobCount];
        for (int job = 0; job < JobCount; job++)
        {
            var definition = context.Jobs[job];
            if (!dueByJob.TryGetValue(definition.Id, out long due))
                throw new ArgumentException($"No target date for job {definition.Id} ('{definition.Reference}').", nameof(dueByJob));
            Due[job] = due;
        }

        bool setups = false;
        bool calendars = false;
        foreach (var machine in Machines)
        {
            setups |= machine.SetupDurations.Count > 0;
            calendars |= machine.AvailabilityWindows.Count > 0 || machine.Blackouts.Count > 0;
        }

        HasSetups = setups;
        HasCalendars = calendars;
        Horizon = ComputeHorizon();
    }

    /// <summary>Global slot indices of a work center: <c>[SlotStart(m), SlotEnd(m))</c>.</summary>
    internal int SlotStart(int machineIndex) => _slotOffsetByMachine[machineIndex];

    /// <inheritdoc cref="SlotStart" />
    internal int SlotEnd(int machineIndex) => _slotOffsetByMachine[machineIndex + 1];

    /// <summary>Parallel slots of a work center.</summary>
    internal int Capacity(int machineIndex) => _slotOffsetByMachine[machineIndex + 1] - _slotOffsetByMachine[machineIndex];

    /// <summary>Change-over charged on <paramref name="machineIndex"/> from one family to another; <c>-1</c> means a slot that has run nothing.</summary>
    internal long Setup(int machineIndex, int fromFamily, int toFamily) =>
        _context.SetupSecondsFor(machineIndex, fromFamily, toFamily);

    /// <summary>The worst change-over that can be charged into <paramref name="operation"/>, over every family in the instance.</summary>
    internal long WorstSetupInto(int operation)
    {
        int machineIndex = MachineOfOperation[operation];
        if (Machines[machineIndex].SetupDurations.Count == 0)
            return 0;

        int toFamily = FamilyOfOperation[operation];
        long worst = 0;
        for (int other = 0; other < OperationCount; other++)
        {
            if (MachineOfOperation[other] != machineIndex)
                continue;
            long seconds = Setup(machineIndex, FamilyOfOperation[other], toFamily);
            if (seconds > worst)
                worst = seconds;
        }

        return worst;
    }

    private long ComputeHorizon()
    {
        long latestRelease = 0;
        foreach (long release in Release)
            latestRelease = Math.Max(latestRelease, release);

        long work = 0;
        for (int operation = 0; operation < OperationCount; operation++)
            work = checked(work + Duration[operation] + WorstSetupInto(operation));

        long latestDue = 0;
        foreach (long due in Due)
            latestDue = Math.Max(latestDue, due);

        // A calendar can only push work later, and by how much is not bounded by
        // the work content, so a horizon that ignores calendars is not an upper
        // bound for an instance that has them. The MILP writer refuses those
        // instances outright; the propagator only ever uses this as a fallback
        // deadline, where being too generous costs strength and not correctness.
        long span = checked(latestRelease + work);
        return Math.Max(span, latestDue) + 1;
    }
}
