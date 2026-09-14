using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Scheduling.Testing;

/// <summary>
/// The twenty instances the published optimality figures are measured on.
/// <para>
/// They exist because the figures were quoted before the study did. Eight
/// documents said "0.2 % mean gap, 19 of 20 instances solved exactly" and pointed
/// at a test that computed neither number on neither set. This is the set: fixed,
/// seeded, committed, and spread over the feature space the engine claims to
/// model — job counts from two to seven, one to five work centers, parallel
/// slots, staggered releases, target dates from loose to impossible, change-over
/// matrices with two and three families, and calendars with shifts, bridgeable
/// breaks and a shutdown.
/// </para>
/// <para>
/// Every instance is small enough that the exact solver proves optimality, because
/// an instance whose optimum is unknown contributes nothing to a gap measurement.
/// That is the honest limit of the study and it is stated rather than hidden: it
/// measures the heuristic where the truth is computable, which is not where the
/// heuristic is normally used.
/// </para>
/// </summary>
public static class OptimalityInstances
{
    private const string FamilyA = "ALU";
    private const string FamilyB = "STEEL";
    private const string FamilyC = "TITAN";
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    /// <summary>How a work center's calendar is shaped.</summary>
    public enum CalendarShape
    {
        /// <summary>Continuously available.</summary>
        None = 0,

        /// <summary>One eight-hour shift a day, no bridging.</summary>
        DayShift = 1,

        /// <summary>Three blocks a day with a bridgeable half-hour break between the first two.</summary>
        ShiftsWithBreak = 2,

        /// <summary>A day shift plus a two-day shutdown that no operation may cross.</summary>
        DayShiftWithShutdown = 3
    }

    /// <summary>One instance of the study, with a label for what it exercises.</summary>
    /// <param name="Name">Stable identifier used as the row key in the report.</param>
    /// <param name="Features">What this instance adds to the set, in words.</param>
    /// <param name="Context">The instance itself.</param>
    public sealed record OptimalityInstance(string Name, string Features, SchedulingContext Context)
    {
        /// <summary>Jobs in the instance.</summary>
        public int JobCount => Context.Jobs.Count;

        /// <summary>Operations across every routing — the size that drives the solver.</summary>
        public int OperationCount
        {
            get
            {
                int total = 0;
                foreach (var job in Context.Jobs)
                    total += job.Steps.Count;
                return total;
            }
        }

        /// <summary>Work centers in the instance.</summary>
        public int WorkCentreCount => Context.Machines.Count;
    }

    /// <summary>The twenty instances, in a fixed order.</summary>
    public static IReadOnlyList<OptimalityInstance> All() =>
    [
        Build(new Shape
        {
            Name = "flow-2x3",
            Features = "the counterexample shape: two routings crossing three work centers",
            Seed = 101, Jobs = 2, Steps = 3, WorkCentres = 3, Twk = 1.0
        }),
        Build(new Shape
        {
            Name = "jobshop-3x3",
            Features = "the textbook three by three",
            Seed = 103, Jobs = 3, Steps = 3, WorkCentres = 3, Twk = 1.2
        }),
        Build(new Shape
        {
            Name = "jobshop-4x3-released",
            Features = "staggered releases",
            Seed = 107, Jobs = 4, Steps = 3, WorkCentres = 3, Releases = true, Twk = 1.5
        }),
        Build(new Shape
        {
            Name = "jobshop-5x3",
            Features = "five routings, no extras",
            Seed = 109, Jobs = 5, Steps = 3, WorkCentres = 3, Twk = 1.5
        }),
        Build(new Shape
        {
            Name = "jobshop-6x3",
            Features = "six routings, loose targets",
            Seed = 113, Jobs = 6, Steps = 3, WorkCentres = 3, Twk = 1.8
        }),
        Build(new Shape
        {
            Name = "jobshop-7x3",
            Features = "the largest instance in the set, twenty-one operations",
            Seed = 127, Jobs = 7, Steps = 3, WorkCentres = 3, Twk = 2.5
        }),
        Build(new Shape
        {
            Name = "bottleneck-6x2",
            Features = "two work centers for six jobs; the shop is the constraint",
            Seed = 131, Jobs = 6, Steps = 2, WorkCentres = 2, Twk = 1.3,
            Rule = DispatchRule.ShortestProcessingTime
        }),
        Build(new Shape
        {
            Name = "wide-5x4",
            Features = "five work centers and four-step routings; little contention",
            Seed = 137, Jobs = 5, Steps = 4, WorkCentres = 5, Twk = 1.6
        }),
        Build(new Shape
        {
            Name = "single-machine-8",
            Features = "one work center, one operation each: the dispatch-order model is exact here",
            Seed = 139, Jobs = 8, Steps = 1, WorkCentres = 1, Twk = 1.4,
            Rule = DispatchRule.WeightedShortestProcessingTime
        }),
        Build(new Shape
        {
            Name = "parallel-6x2-cap2",
            Features = "two slots per work center",
            Seed = 149, Jobs = 6, Steps = 2, WorkCentres = 2, Capacity = 2, Twk = 1.4
        }),
        Build(new Shape
        {
            Name = "parallel-6x3-cap3",
            Features = "three slots per work center, longest-processing-time rule",
            Seed = 151, Jobs = 6, Steps = 3, WorkCentres = 3, Capacity = 3, Twk = 1.5,
            Rule = DispatchRule.LongestProcessingTime
        }),
        Build(new Shape
        {
            Name = "setup-2fam-5x2",
            Features = "two change-over families, fifteen minutes each way",
            Seed = 157, Jobs = 5, Steps = 2, WorkCentres = 2, Families = 2, Twk = 1.5
        }),
        Build(new Shape
        {
            Name = "setup-3fam-6x2",
            Features = "three families, asymmetric change-over matrix",
            Seed = 163, Jobs = 6, Steps = 2, WorkCentres = 3, Families = 3, Twk = 1.6
        }),
        Build(new Shape
        {
            Name = "setup-heavy-4x3",
            Features = "change-over as long as an operation; grouping is the whole game",
            Seed = 167, Jobs = 4, Steps = 3, WorkCentres = 2, Families = 2, SetupSeconds = 2_700, Twk = 1.4
        }),
        Build(new Shape
        {
            Name = "calendar-dayshift-4x3",
            Features = "one eight-hour shift a day, constant allowance targets",
            Seed = 173, Jobs = 4, Steps = 3, WorkCentres = 3, Calendar = CalendarShape.DayShift,
            DueRule = DueDateRule.ConstantAllowance, ConstantAllowance = 3 * Day
        }),
        Build(new Shape
        {
            Name = "calendar-breaks-5x2",
            Features = "shifts with a bridgeable break an operation may pause across",
            Seed = 179, Jobs = 5, Steps = 2, WorkCentres = 2, Calendar = CalendarShape.ShiftsWithBreak,
            DueRule = DueDateRule.ConstantAllowance, ConstantAllowance = 2 * Day
        }),
        Build(new Shape
        {
            Name = "calendar-shutdown-4x3",
            Features = "a two-day shutdown no operation may cross, plus releases",
            Seed = 181, Jobs = 4, Steps = 3, WorkCentres = 3, Releases = true,
            Calendar = CalendarShape.DayShiftWithShutdown,
            DueRule = DueDateRule.ConstantAllowance, ConstantAllowance = 5 * Day
        }),
        Build(new Shape
        {
            Name = "duedates-tight-6x2",
            Features = "explicit target dates most jobs cannot meet; the late-job term dominates",
            Seed = 191, Jobs = 6, Steps = 2, WorkCentres = 3, Releases = true,
            DueRule = DueDateRule.Explicit, ExplicitFactor = 1.1
        }),
        Build(new Shape
        {
            Name = "duedates-slack-7x2",
            Features = "equal-slack targets and the weighted rule",
            Seed = 193, Jobs = 7, Steps = 2, WorkCentres = 3, DueRule = DueDateRule.EqualSlack,
            SlackSeconds = 1_800, Rule = DispatchRule.WeightedShortestProcessingTime
        }),
        Build(new Shape
        {
            Name = "mixed-5x3",
            Features = "change-over, parallel slots, releases and a shift calendar at once",
            Seed = 197, Jobs = 5, Steps = 3, WorkCentres = 3, Capacity = 2, Families = 2,
            Releases = true, Calendar = CalendarShape.DayShift,
            DueRule = DueDateRule.ConstantAllowance, ConstantAllowance = 4 * Day
        })
    ];

    private sealed record Shape
    {
        public required string Name { get; init; }
        public required string Features { get; init; }
        public required int Seed { get; init; }
        public required int Jobs { get; init; }
        public required int Steps { get; init; }
        public required int WorkCentres { get; init; }
        public int Capacity { get; init; } = 1;
        public bool Releases { get; init; }
        public CalendarShape Calendar { get; init; }
        public int Families { get; init; } = 1;
        public long SetupSeconds { get; init; } = 900;
        public DueDateRule DueRule { get; init; } = DueDateRule.TotalWorkContent;
        public double Twk { get; init; } = 1.5;
        public long SlackSeconds { get; init; } = 7_200;
        public long ConstantAllowance { get; init; } = 8 * Hour;
        public double? ExplicitFactor { get; init; }
        public DispatchRule Rule { get; init; } = DispatchRule.EarliestDueDate;
    }

    private static OptimalityInstance Build(Shape shape)
    {
        var random = new DeterministicRandom(shape.Seed);
        var families = Families(shape.Families);

        var machines = new List<MachineCapacity>(shape.WorkCentres);
        for (int id = 1; id <= shape.WorkCentres; id++)
            machines.Add(WorkCentre(id, shape, families));

        var jobs = new List<ProductionJob>(shape.Jobs);
        for (int index = 0; index < shape.Jobs; index++)
        {
            var steps = new List<JobStep>(shape.Steps);
            long work = 0;
            for (int step = 0; step < shape.Steps; step++)
            {
                // Spread the routing over the work centers rather than letting the
                // PRNG pile three consecutive steps onto one: a routing that never
                // moves is a single-machine problem wearing a job-shop label.
                int workCentre = (index + step * 2 + random.NextInt(shape.WorkCentres)) % shape.WorkCentres + 1;
                long duration = 600 + random.NextInt(9) * 300L;
                work += duration;
                steps.Add(new JobStep((step + 1) * 10, workCentre, duration, families[(index + step) % families.Length]));
            }

            long release = shape.Releases ? (index % 4) * 1_800L : 0;
            jobs.Add(new ProductionJob
            {
                Id = index + 1,
                Reference = $"JOB-{index + 1:00}",
                ReleaseSeconds = release,
                Weight = 1 + random.NextInt(5),
                ExplicitDueSeconds = shape.ExplicitFactor is { } factor
                    ? release + (long)(factor * work)
                    : null,
                Steps = steps
            });
        }

        var parameters = new SchedulingParameters
        {
            DispatchRule = shape.Rule,
            DueDateRule = shape.DueRule,
            TwkFlowFactor = shape.Twk,
            SlackSeconds = shape.SlackSeconds,
            ConstantAllowanceSeconds = shape.ConstantAllowance,
            Seed = shape.Seed
        };

        return new OptimalityInstance(shape.Name, shape.Features, new SchedulingContext(jobs, machines, parameters));
    }

    private static string[] Families(int count) => count switch
    {
        2 => [FamilyA, FamilyB],
        3 => [FamilyA, FamilyB, FamilyC],
        _ => [JobStep.DefaultSetupFamily]
    };

    private static MachineCapacity WorkCentre(int id, Shape shape, string[] families)
    {
        var setups = new List<SetupDuration>();
        for (int from = 0; from < families.Length; from++)
        {
            for (int to = 0; to < families.Length; to++)
            {
                if (from == to || families.Length == 1)
                    continue;

                // Asymmetric on purpose: a matrix that is symmetric lets a wrong
                // model look right, because the order within a pair stops mattering.
                setups.Add(new SetupDuration(families[from], families[to], shape.SetupSeconds + from * 300L));
            }
        }

        var machine = new MachineCapacity(id, $"WC-{id:00}", shape.Capacity) { SetupDurations = setups };

        return shape.Calendar switch
        {
            CalendarShape.DayShift => machine with
            {
                AvailabilityWindows = [new CapacityWindow(6 * Hour, 14 * Hour)],
                CalendarPeriodSeconds = Day
            },
            CalendarShape.ShiftsWithBreak => machine with
            {
                AvailabilityWindows =
                [
                    new CapacityWindow(6 * Hour, 10 * Hour),
                    new CapacityWindow(10 * Hour + 1_800, 14 * Hour),
                    new CapacityWindow(14 * Hour, 18 * Hour)
                ],
                CalendarPeriodSeconds = Day,
                MaxBridgeableGapSeconds = 1_800
            },
            CalendarShape.DayShiftWithShutdown => machine with
            {
                AvailabilityWindows = [new CapacityWindow(6 * Hour, 14 * Hour)],
                CalendarPeriodSeconds = Day,
                Blackouts = [new CapacityBlackout(2 * Day, 4 * Day, "shutdown")]
            },
            _ => machine
        };
    }
}
