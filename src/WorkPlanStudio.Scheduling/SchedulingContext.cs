namespace WorkPlanStudio.Scheduling;

/// <summary>
/// An immutable bundle of everything one scheduling run needs: the jobs, the work
/// centers and their capacities, and the parameters. Validates its inputs on
/// construction so the rest of the engine can assume well-formed data (the app's
/// mapping layer is responsible for filtering inactive work centers and empty
/// routings before building a context).
/// <para>
/// "Validated on construction" is an invariant here, not a hope: the jobs and
/// machines are copied, so a caller that still holds the list it passed in cannot
/// add a duplicate id or a negative duration afterwards and have the engine trip
/// over it from inside the search loop.
/// </para>
/// <para>
/// The constructor also flattens the instance into the dense integer arrays the
/// dispatcher walks — steps by index, work centers by index, setup families
/// interned to ints. That work happens once per context instead of once per
/// candidate schedule, and it is what lets a candidate be evaluated without
/// allocating.
/// </para>
/// </summary>
public sealed class SchedulingContext
{
    /// <summary>The jobs to schedule (may be empty → an empty schedule).</summary>
    public IReadOnlyList<ProductionJob> Jobs { get; }

    /// <summary>Work-center capacities, keyed by work-center id.</summary>
    public IReadOnlyDictionary<int, MachineCapacity> Machines { get; }

    /// <summary>The run parameters.</summary>
    public SchedulingParameters Parameters { get; }

    // ----- flattened model, built once, read by the dispatcher -----

    private readonly long[] _longestPlacementByMachineIndex;
    private readonly Dictionary<string, int> _familyIds;
    private readonly bool[] _machineHasSetup;
    private readonly Dictionary<long, long> _setupByKey;
    private readonly int _familyCount;

    /// <summary>Validates the inputs and builds an immutable scheduling context.</summary>
    public SchedulingContext(
        IReadOnlyList<ProductionJob> jobs,
        IReadOnlyList<MachineCapacity> machines,
        SchedulingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(parameters);
        SchedulingParameterLimits.Validate(parameters);

        var jobList = jobs.ToArray();
        var machineList = machines.ToArray();

        var byId = new Dictionary<int, MachineCapacity>(machineList.Length);
        _familyIds = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var m in machineList)
        {
            ArgumentNullException.ThrowIfNull(m);
            ValidateMachine(m);

            // Every other input error in this constructor throws; a work center
            // listed twice used to win silently, which quietly removes capacity
            // from the shop and still produces a plausible-looking schedule.
            if (!byId.TryAdd(m.WorkCenterId, m))
                throw new ArgumentException($"Work center {m.WorkCenterId} is declared more than once.");

            foreach (var setup in m.SetupDurations)
            {
                InternFamily(setup.FromFamily);
                InternFamily(setup.ToFamily);
            }
        }

        MachineIdsSorted = [.. byId.Keys.Order()];
        MachinesByIndex = new MachineCapacity[MachineIdsSorted.Length];
        SlotOffsetByMachineIndex = new int[MachineIdsSorted.Length + 1];
        _longestPlacementByMachineIndex = new long[MachineIdsSorted.Length];
        _machineHasSetup = new bool[MachineIdsSorted.Length];
        for (int i = 0; i < MachineIdsSorted.Length; i++)
        {
            var machine = byId[MachineIdsSorted[i]];
            MachinesByIndex[i] = machine;
            SlotOffsetByMachineIndex[i + 1] = SlotOffsetByMachineIndex[i] + machine.ParallelCapacity;
            _longestPlacementByMachineIndex[i] = machine.LongestPlacementSeconds;
            _machineHasSetup[i] = machine.SetupDurations.Count > 0;
        }

        // Jobs: identity, shape and magnitude first, so the flattening below can
        // assume well-formed data and the calendar-fit check can see every family
        // a work center actually runs.
        var seenJobIds = new HashSet<int>(jobList.Length);
        int totalSteps = 0;
        foreach (var job in jobList)
        {
            ArgumentNullException.ThrowIfNull(job);
            ValidateJobHeader(job, seenJobIds);
            totalSteps += job.Steps.Count;
        }

        JobStepOffset = new int[jobList.Length + 1];
        StepMachineIndex = new int[totalSteps];
        StepDuration = new long[totalSteps];
        StepFamilyId = new int[totalSteps];
        StepNumbers = new int[totalSteps];
        JobRelease = new long[jobList.Length];

        var familiesOnMachine = new HashSet<int>[MachinesByIndex.Length];
        for (int i = 0; i < familiesOnMachine.Length; i++)
            familiesOnMachine[i] = [];

        long totalWork = 0;
        long latestRelease = 0;
        int cursor = 0;
        for (int j = 0; j < jobList.Length; j++)
        {
            var job = jobList[j];
            JobRelease[j] = job.ReleaseSeconds;
            JobStepOffset[j] = cursor;
            latestRelease = Math.Max(latestRelease, job.ReleaseSeconds);

            long previousStepNumber = long.MinValue;
            foreach (var step in job.Steps)
            {
                ArgumentNullException.ThrowIfNull(step);
                if (step.StepNumber <= previousStepNumber)
                    throw new ArgumentException($"Job {job.Id} steps must have strictly increasing step numbers.");
                previousStepNumber = step.StepNumber;

                if (step.DurationSeconds < 0)
                    throw new ArgumentException($"Job {job.Id} step {step.StepNumber} has negative duration.");
                if (step.DurationSeconds > SchedulingParameterLimits.MaxStepDurationSeconds)
                    throw new ArgumentException(
                        $"Job {job.Id} step {step.StepNumber} lasts {step.DurationSeconds}s, " +
                        $"over the {SchedulingParameterLimits.MaxStepDurationSeconds}s limit on a single operation.");

                if (string.IsNullOrWhiteSpace(step.SetupFamily) || step.SetupFamily.Length > 40)
                    throw new ArgumentException($"Job {job.Id} step {step.StepNumber} has an invalid setup family.");

                if (!byId.ContainsKey(step.WorkCenterId))
                    throw new ArgumentException($"Job {job.Id} step {step.StepNumber} references unknown work center {step.WorkCenterId}.");

                int machineIndex = Array.BinarySearch(MachineIdsSorted, step.WorkCenterId);
                int familyId = InternFamily(step.SetupFamily);
                familiesOnMachine[machineIndex].Add(familyId);

                StepMachineIndex[cursor] = machineIndex;
                StepDuration[cursor] = step.DurationSeconds;
                StepFamilyId[cursor] = familyId;
                StepNumbers[cursor] = step.StepNumber;
                cursor++;

                totalWork = checked(totalWork + step.DurationSeconds);
            }
        }

        JobStepOffset[jobList.Length] = cursor;

        // The whole instance has to fit the horizon, not only each step: jobs
        // compose on a shared machine clock, and that clock is what overflows.
        long span = checked(totalWork + latestRelease);
        if (span > SchedulingParameterLimits.MaxHorizonSeconds)
            throw new ArgumentException(
                $"The instance spans {span}s of work after its latest release, " +
                $"over the {SchedulingParameterLimits.MaxHorizonSeconds}s planning horizon.");

        _familyCount = _familyIds.Count;
        _setupByKey = BuildSetupLookup(byId);

        // A step must fit inside one availability window, or one run of windows
        // joined by bridgeable gaps - worst-case change-over included. Checked
        // here rather than during dispatch: the search evaluates thousands of
        // candidate orders, and an exception thrown from inside that loop would
        // abort the whole run instead of reporting an input problem the caller
        // can act on.
        for (int j = 0; j < jobList.Length; j++)
        {
            for (int s = JobStepOffset[j]; s < JobStepOffset[j + 1]; s++)
            {
                int machineIndex = StepMachineIndex[s];
                long longestPlacement = _longestPlacementByMachineIndex[machineIndex];
                if (longestPlacement == long.MaxValue)
                    continue;

                long needed = StepDuration[s] + WorstSetupInto(machineIndex, StepFamilyId[s], familiesOnMachine[machineIndex]);
                if (needed > longestPlacement)
                    throw new ArgumentException(
                        $"Job {jobList[j].Id} step {StepNumbers[s]} needs {needed}s including change-over, " +
                        $"but the longest availability window of work center {MachineIdsSorted[machineIndex]} is {longestPlacement}s.");
            }
        }

        Jobs = jobList;
        Machines = byId;
        Parameters = parameters;
    }

    /// <summary>Copy constructor for a context that differs only in its parameters.</summary>
    private SchedulingContext(SchedulingContext source, SchedulingParameters parameters)
    {
        SchedulingParameterLimits.Validate(parameters);

        Jobs = source.Jobs;
        Machines = source.Machines;
        Parameters = parameters;

        MachinesByIndex = source.MachinesByIndex;
        MachineIdsSorted = source.MachineIdsSorted;
        SlotOffsetByMachineIndex = source.SlotOffsetByMachineIndex;
        _longestPlacementByMachineIndex = source._longestPlacementByMachineIndex;
        JobStepOffset = source.JobStepOffset;
        StepMachineIndex = source.StepMachineIndex;
        StepDuration = source.StepDuration;
        StepFamilyId = source.StepFamilyId;
        StepNumbers = source.StepNumbers;
        JobRelease = source.JobRelease;
        _familyIds = source._familyIds;
        _machineHasSetup = source._machineHasSetup;
        _setupByKey = source._setupByKey;
        _familyCount = source._familyCount;
    }

    /// <summary>
    /// The same instance under different parameters, reusing this context's
    /// validated and flattened model.
    /// </summary>
    /// <remarks>
    /// Rebuilding a <see cref="SchedulingContext"/> to change one enum is using
    /// the most expensive object in the library as a parameter bag: it re-runs
    /// every input check, including the per-step calendar fit. The rule probes in
    /// <see cref="ScheduleExplainer"/> and <see cref="PriorityOrdering"/> do that
    /// on every run, which is why this exists. Only the parameters are validated
    /// again — the jobs and machines cannot have changed, because the context owns
    /// its copies of them.
    /// </remarks>
    public SchedulingContext WithParameters(SchedulingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new SchedulingContext(this, parameters);
    }

    /// <summary>
    /// Change-over cost on <paramref name="workCenterId"/> when the slot last ran
    /// <paramref name="from"/> and is about to run <paramref name="to"/>. Zero for
    /// a fresh slot, for an unchanged family, or for a transition the work center
    /// does not list.
    /// </summary>
    public long SetupSecondsFor(int workCenterId, string? from, string to)
    {
        ArgumentNullException.ThrowIfNull(to);
        if (from is null || string.Equals(from, to, StringComparison.Ordinal))
            return 0;

        int machineIndex = Array.BinarySearch(MachineIdsSorted, workCenterId);
        if (machineIndex < 0 ||
            !_familyIds.TryGetValue(from, out int fromId) ||
            !_familyIds.TryGetValue(to, out int toId))
            return 0;

        return SetupSecondsFor(machineIndex, fromId, toId);
    }

    /// <summary>Parallel-slot count for a work center (defaults to 1 if unknown).</summary>
    public int CapacityOf(int workCenterId) =>
        Machines.TryGetValue(workCenterId, out var m) ? m.ParallelCapacity : 1;

    // ----- internal flattened accessors (the dispatcher's hot path) -----

    internal MachineCapacity[] MachinesByIndex { get; }
    internal int[] MachineIdsSorted { get; }
    internal int[] SlotOffsetByMachineIndex { get; }
    internal int TotalSlots => SlotOffsetByMachineIndex[^1];
    internal int[] JobStepOffset { get; }
    internal int[] StepMachineIndex { get; }
    internal long[] StepDuration { get; }
    internal int[] StepFamilyId { get; }
    internal int[] StepNumbers { get; }
    internal long[] JobRelease { get; }
    internal int TotalSteps => JobStepOffset.Length == 0 ? 0 : JobStepOffset[^1];

    /// <summary>Change-over cost by dense index; <c>-1</c> means a slot that has run nothing yet.</summary>
    internal long SetupSecondsFor(int machineIndex, int fromFamilyId, int toFamilyId)
    {
        if (fromFamilyId < 0 || fromFamilyId == toFamilyId || !_machineHasSetup[machineIndex])
            return 0;

        long key = ((long)machineIndex * _familyCount + fromFamilyId) * _familyCount + toFamilyId;
        return _setupByKey.TryGetValue(key, out long seconds) ? seconds : 0;
    }

    private Dictionary<long, long> BuildSetupLookup(Dictionary<int, MachineCapacity> byId)
    {
        var lookup = new Dictionary<long, long>();
        for (int i = 0; i < MachineIdsSorted.Length; i++)
        {
            var machine = byId[MachineIdsSorted[i]];
            foreach (var setup in machine.SetupDurations)
            {
                long key = ((long)i * _familyCount + _familyIds[setup.FromFamily]) * _familyCount + _familyIds[setup.ToFamily];
                lookup[key] = setup.DurationSeconds;
            }
        }

        return lookup;
    }

    /// <summary>
    /// The worst change-over into <paramref name="toFamilyId"/> that this work
    /// center can actually be charged in this instance.
    /// </summary>
    /// <remarks>
    /// Taking the maximum over the whole declared matrix instead is what made a
    /// single long transition from a family no job uses reject every job on the
    /// center. Setup matrices are written once per work center for every family it
    /// could ever run; only the families that occur here can be charged.
    /// </remarks>
    private long WorstSetupInto(int machineIndex, int toFamilyId, HashSet<int> familiesHere)
    {
        if (!_machineHasSetup[machineIndex])
            return 0;

        long worst = 0;
        foreach (int fromFamilyId in familiesHere)
        {
            long seconds = SetupSecondsFor(machineIndex, fromFamilyId, toFamilyId);
            if (seconds > worst)
                worst = seconds;
        }

        return worst;
    }

    private int InternFamily(string family)
    {
        if (_familyIds.TryGetValue(family, out int id))
            return id;

        id = _familyIds.Count;
        _familyIds[family] = id;
        return id;
    }

    private static void ValidateMachine(MachineCapacity m)
    {
        if (m.ParallelCapacity is < 1 or > 64)
            throw new ArgumentException($"Work center {m.WorkCenterId} has invalid capacity {m.ParallelCapacity} (must be 1..64).");

        if (m.AvailabilityWindows.Count > 0 && m.CalendarPeriodSeconds <= 0)
            throw new ArgumentException($"Work center {m.WorkCenterId} declares availability windows but no calendar period.");

        if (m.CalendarPeriodSeconds > SchedulingParameterLimits.MaxStepDurationSeconds)
            throw new ArgumentException(
                $"Work center {m.WorkCenterId} declares a {m.CalendarPeriodSeconds}s calendar period, " +
                $"over the {SchedulingParameterLimits.MaxStepDurationSeconds}s limit.");

        long previousEnd = -1;
        foreach (var window in m.AvailabilityWindows)
        {
            window.Validate();
            if (window.StartSeconds < previousEnd)
                throw new ArgumentException($"Work center {m.WorkCenterId} availability windows must be sorted and non-overlapping.");
            if (window.EndSeconds > m.CalendarPeriodSeconds)
                throw new ArgumentException(
                    $"Work center {m.WorkCenterId} window [{window.StartSeconds}, {window.EndSeconds}) " +
                    $"does not fit inside its {m.CalendarPeriodSeconds}s calendar period.");
            previousEnd = window.EndSeconds;
        }

        if (m.MaxBridgeableGapSeconds < 0 || (m.AvailabilityWindows.Count > 0 && m.MaxBridgeableGapSeconds >= m.CalendarPeriodSeconds))
            throw new ArgumentException($"Work center {m.WorkCenterId} bridgeable gap must lie inside [0, period).");

        if (m.CalendarPhaseSeconds < 0 ||
            (m.AvailabilityWindows.Count > 0 && m.CalendarPhaseSeconds >= m.CalendarPeriodSeconds) ||
            (m.AvailabilityWindows.Count == 0 && m.CalendarPhaseSeconds != 0))
            throw new ArgumentException(
                $"Work center {m.WorkCenterId} calendar phase {m.CalendarPhaseSeconds}s must lie inside [0, period).");

        long previousBlackoutEnd = -1;
        foreach (var blackout in m.Blackouts)
        {
            blackout.Validate();
            if (blackout.StartSeconds < previousBlackoutEnd)
                throw new ArgumentException($"Work center {m.WorkCenterId} blackouts must be sorted and non-overlapping.");
            if (blackout.EndSeconds > SchedulingParameterLimits.MaxHorizonSeconds)
                throw new ArgumentException(
                    $"Work center {m.WorkCenterId} blackout ends at {blackout.EndSeconds}s, " +
                    $"past the {SchedulingParameterLimits.MaxHorizonSeconds}s planning horizon.");
            previousBlackoutEnd = blackout.EndSeconds;
        }

        foreach (var setup in m.SetupDurations)
            setup.Validate();
    }

    private static void ValidateJobHeader(ProductionJob job, HashSet<int> seenJobIds)
    {
        // A duplicate id is not a harmless repeat: target dates are keyed by id,
        // so one job silently inherits the other's, and the two rows the schedule
        // reports can no longer be told apart. Measured, it moved the penalty by a
        // factor of 731 depending on which of the two came first in the list.
        if (!seenJobIds.Add(job.Id))
            throw new ArgumentException($"Duplicate job id {job.Id} ('{job.Reference}').");

        if (string.IsNullOrWhiteSpace(job.Reference) || job.Reference.Length > 80)
            throw new ArgumentException($"Job {job.Id} needs a reference of at most 80 characters.");

        if (job.Steps.Count == 0)
            throw new ArgumentException($"Job {job.Id} ('{job.Reference}') has no steps.");

        // A negative weight sorts a job last under WSPT and a NaN weight sorts it
        // first, because NaN compares below every number. Neither is a defensible
        // reading of "relative importance".
        if (!double.IsFinite(job.Weight) || job.Weight <= 0)
            throw new ArgumentException($"Job {job.Id} has invalid weight {job.Weight} (must be finite and greater than 0).");

        // Second 0 is the horizon by definition. The dispatcher's calendar walk,
        // the blackout search and the open-time roll-up all assume a non-negative
        // axis, and integer division truncates towards zero, so a negative release
        // quietly lands in the wrong window rather than before the horizon.
        if (job.ReleaseSeconds < 0 || job.ReleaseSeconds > SchedulingParameterLimits.MaxHorizonSeconds)
            throw new ArgumentException(
                $"Job {job.Id} is released at {job.ReleaseSeconds}s, outside " +
                $"[0, {SchedulingParameterLimits.MaxHorizonSeconds}].");

        if (job.ExplicitDueSeconds is { } due)
        {
            if (due < 0 || due > SchedulingParameterLimits.MaxHorizonSeconds)
                throw new ArgumentException(
                    $"Job {job.Id} has target date {due}s, outside [0, {SchedulingParameterLimits.MaxHorizonSeconds}].");
            if (due < job.ReleaseSeconds)
                throw new ArgumentException(
                    $"Job {job.Id} has target date {due}s before its release at {job.ReleaseSeconds}s.");
        }
    }
}
