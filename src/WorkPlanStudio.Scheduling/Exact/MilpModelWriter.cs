using System.Globalization;
using System.Text;

namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// Writes the instance as a mixed-integer program in CPLEX LP format, so that the
/// optimum this library reports can be checked by somebody who does not trust this
/// library.
/// <para>
/// That is the whole point of the file. A solver that grades its own homework is
/// how the repository ended up quoting a 50 % gap as an optimality result. Feeding
/// the emitted model to HiGHS or CBC and comparing the objective is a check that
/// shares no code, no author and no assumptions with the branch-and-bound —
/// <c>docs/adr/0015-exact-solver.md</c> gives the two commands.
/// </para>
/// <para>
/// <b>Disjunctive big-M, not time-indexed.</b> A time-indexed model has one binary
/// per operation per time step, and time here is integer seconds over a horizon of
/// days: a twenty-operation instance would need tens of millions of binaries, and
/// rounding the grid to make it tractable would change the problem. The
/// disjunctive model needs one binary per pair of operations that share a work
/// center, which is a few hundred at these sizes. Its big-M is the instance
/// horizon — the latest release plus every operation plus the worst change-over
/// into each — so it is derived from the data rather than chosen, and no schedule
/// the model should admit is cut off by it.
/// </para>
/// <para>
/// <b>What it refuses.</b> Availability windows and blackouts are not expressible
/// in this formulation: "closed at night" is a property of the time axis, and a
/// continuous start variable cannot carry it without the time-indexed model this
/// decision rejects. A work center that has both several parallel slots and a
/// change-over matrix is refused for a narrower reason: the change-over arcs below
/// form one path per slot, and the number of operations on a slot is itself a
/// decision. Both are refused loudly rather than approximated silently.
/// </para>
/// </summary>
public static class MilpModelWriter
{
    /// <summary>
    /// True when <paramref name="context"/> can be written exactly;
    /// <paramref name="reason"/> says why not otherwise.
    /// </summary>
    public static bool CanWrite(SchedulingContext context, out string reason)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var machine in context.Machines.Values)
        {
            if (machine.AvailabilityWindows.Count > 0 || machine.Blackouts.Count > 0)
            {
                reason =
                    $"Work center {machine.WorkCenterId} declares a calendar. The disjunctive formulation " +
                    "has no way to express closed time on a continuous start variable; that needs a " +
                    "time-indexed model, which this writer deliberately does not emit.";
                return false;
            }

            if (machine.ParallelCapacity > 1 && machine.SetupDurations.Count > 0)
            {
                reason =
                    $"Work center {machine.WorkCenterId} has {machine.ParallelCapacity} parallel slots and a " +
                    "change-over matrix. Change-over is charged along the sequence on one slot, and a " +
                    "per-slot sequence whose length is itself a decision is a different formulation.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>The instance as an LP-format model.</summary>
    /// <exception cref="NotSupportedException">The instance has a feature the formulation cannot express exactly.</exception>
    public static string Write(SchedulingContext context)
    {
        var builder = new StringBuilder();
        using (var writer = new StringWriter(builder, CultureInfo.InvariantCulture))
            WriteTo(context, writer);

        return builder.ToString();
    }

    /// <summary>Writes the model to <paramref name="output"/>.</summary>
    /// <exception cref="NotSupportedException">The instance has a feature the formulation cannot express exactly.</exception>
    public static void WriteTo(SchedulingContext context, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        if (!CanWrite(context, out string reason))
            throw new NotSupportedException(reason);

        var dueByJob = DueDateAssigner.Assign(context);
        var instance = new ExactInstance(context, dueByJob);
        new Model(instance).WriteTo(output);
    }

    /// <summary>
    /// The big-M this writer would use for <paramref name="context"/>: the instance
    /// horizon. Exposed so a test can assert the constant is derived and not chosen.
    /// </summary>
    public static long BigM(SchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ExactInstance(context, DueDateAssigner.Assign(context)).Horizon;
    }

    private sealed class Model(ExactInstance instance)
    {
        private readonly ExactInstance _instance = instance;
        private readonly long[] _head = Heads(instance);
        private readonly List<string> _binaries = [];

        private long BigM => _instance.Horizon;

        internal void WriteTo(TextWriter output)
        {
            using var body = new StringWriter(CultureInfo.InvariantCulture);
            WriteConstraints(body);

            WritePreamble(output);
            WriteObjective(output);
            output.WriteLine("Subject To");
            output.Write(body.ToString());
            WriteBounds(output);
            WriteBinaries(output);
            output.WriteLine("End");
        }

        private void WritePreamble(TextWriter output)
        {
            var parameters = _instance.Parameters;
            output.WriteLine("\\ WorkPlan Studio - job shop, disjunctive big-M formulation.");
            output.WriteLine($"\\ {_instance.JobCount} jobs, {_instance.OperationCount} operations, {_instance.MachineCount} work centers, {_instance.SlotCount} slots.");
            output.WriteLine($"\\ Big-M = {BigM} (the instance horizon: latest release + every operation + the worst change-over into each).");
            output.WriteLine("\\ Times are integer seconds from the planning horizon.");
            output.WriteLine("\\ The objective is the engine's penalty:");
            output.WriteLine($"\\   {Number(parameters.MakespanWeight)} * makespan/3600 + {Number(parameters.TardinessWeight)} * tardiness/3600 + {Number(parameters.LatePenalty)} * late jobs.");
            output.WriteLine("\\ Variables: s_o operation start, C_j job completion, T_j job tardiness,");
            output.WriteLine("\\ U_j job is late, Cmax makespan, x_i_j i precedes j, y_o_s slot assignment,");
            output.WriteLine("\\ z_i_j j runs immediately after i on the same work center (change-over).");
            output.WriteLine();
        }

        private void WriteObjective(TextWriter output)
        {
            var parameters = _instance.Parameters;
            var terms = new List<string>();

            if (parameters.MakespanWeight != 0)
                terms.Add($"{Number(parameters.MakespanWeight / 3600.0)} Cmax");

            if (parameters.TardinessWeight != 0)
            {
                for (int job = 0; job < _instance.JobCount; job++)
                    terms.Add($"{Number(parameters.TardinessWeight / 3600.0)} T_{job}");
            }

            if (parameters.LatePenalty != 0)
            {
                for (int job = 0; job < _instance.JobCount; job++)
                    terms.Add($"{Number(parameters.LatePenalty)} U_{job}");
            }

            // An empty objective is not valid LP, and a zero-weighted instance is a
            // legitimate (if pointless) input.
            if (terms.Count == 0)
                terms.Add("0 Cmax");

            output.WriteLine("Minimize");
            output.WriteLine($" obj: {string.Join(" + ", terms)}");
            output.WriteLine();
        }

        private void WriteConstraints(TextWriter output)
        {
            var instance = _instance;

            for (int job = 0; job < instance.JobCount; job++)
            {
                int first = instance.JobFirst[job];
                int last = instance.JobFirst[job + 1] - 1;

                for (int operation = first; operation < last; operation++)
                    Constraint(output, $"prec_{operation}", $"s_{operation + 1} - s_{operation}", ">=", instance.Duration[operation]);

                Constraint(output, $"done_{job}", $"C_{job} - s_{last}", "=", instance.Duration[last]);
                Constraint(output, $"tard_{job}", $"T_{job} - C_{job}", ">=", -instance.Due[job]);
                Constraint(output, $"late_{job}", $"C_{job} - {Number(BigM)} U_{job}", "<=", instance.Due[job]);
                Constraint(output, $"span_{job}", $"Cmax - C_{job}", ">=", 0);
                _binaries.Add($"U_{job}");
            }

            for (int machine = 0; machine < instance.MachineCount; machine++)
                WriteWorkCentre(output, machine);
        }

        private void WriteWorkCentre(TextWriter output, int machine)
        {
            var instance = _instance;
            var operations = new List<int>();
            for (int operation = 0; operation < instance.OperationCount; operation++)
            {
                if (instance.MachineOfOperation[operation] == machine)
                    operations.Add(operation);
            }

            if (operations.Count < 2)
                return;

            int capacity = instance.Capacity(machine);
            bool hasSetup = instance.Machines[machine].SetupDurations.Count > 0;
            long m = BigM;

            if (capacity > 1)
            {
                foreach (int operation in operations)
                {
                    var terms = new List<string>();
                    for (int slot = 0; slot < capacity; slot++)
                    {
                        terms.Add($"y_{operation}_{slot}");
                        _binaries.Add($"y_{operation}_{slot}");
                    }

                    Constraint(output, $"assign_{operation}", string.Join(" + ", terms), "=", 1);
                }
            }

            for (int a = 0; a < operations.Count; a++)
            {
                for (int b = a + 1; b < operations.Count; b++)
                {
                    int i = operations[a];
                    int j = operations[b];
                    _binaries.Add($"x_{i}_{j}");

                    if (capacity == 1)
                    {
                        // x = 1: j starts after i finishes. x = 0: the other way.
                        Constraint(output, $"seq_{i}_{j}_a", $"s_{j} - s_{i} - {Number(m)} x_{i}_{j}", ">=", instance.Duration[i] - m);
                        Constraint(output, $"seq_{i}_{j}_b", $"s_{i} - s_{j} + {Number(m)} x_{i}_{j}", ">=", instance.Duration[j]);
                        continue;
                    }

                    // Several slots: the pair only conflicts when both land on the
                    // same one, so each slot gets its own relaxed pair of rows.
                    for (int slot = 0; slot < capacity; slot++)
                    {
                        Constraint(
                            output, $"seq_{i}_{j}_{slot}_a",
                            $"s_{j} - s_{i} - {Number(m)} x_{i}_{j} - {Number(m)} y_{i}_{slot} - {Number(m)} y_{j}_{slot}",
                            ">=", instance.Duration[i] - 3 * m);
                        Constraint(
                            output, $"seq_{i}_{j}_{slot}_b",
                            $"s_{i} - s_{j} + {Number(m)} x_{i}_{j} - {Number(m)} y_{i}_{slot} - {Number(m)} y_{j}_{slot}",
                            ">=", instance.Duration[j] - 2 * m);
                    }
                }
            }

            if (!hasSetup)
                return;

            // Change-over is charged along the sequence, not between every pair: a
            // change-over matrix need not satisfy the triangle inequality, so
            // charging i -> k directly when k merely follows i would forbid
            // schedules that are perfectly feasible. z_i_j is "j runs immediately
            // after i", and the degree and count rows below make those arcs the one
            // Hamiltonian path that the x ordering already fixes.
            var arcs = new List<string>();
            foreach (int i in operations)
            {
                var outgoing = new List<string>();
                foreach (int j in operations)
                {
                    if (i == j)
                        continue;

                    string arc = $"z_{i}_{j}";
                    outgoing.Add(arc);
                    arcs.Add(arc);
                    _binaries.Add(arc);

                    long setup = instance.Setup(machine, instance.FamilyOfOperation[i], instance.FamilyOfOperation[j]);
                    Constraint(
                        output, $"chg_{i}_{j}",
                        $"s_{j} - s_{i} - {Number(m)} {arc}", ">=", instance.Duration[i] + setup - m);

                    // An arc may only follow the ordering the sequencing rows chose.
                    Constraint(
                        output, $"arcord_{i}_{j}",
                        i < j ? $"{arc} - x_{i}_{j}" : $"{arc} + x_{j}_{i}",
                        "<=", i < j ? 0 : 1);
                }

                Constraint(output, $"outdeg_{i}", string.Join(" + ", outgoing), "<=", 1);
            }

            foreach (int j in operations)
            {
                var incoming = new List<string>();
                foreach (int i in operations)
                {
                    if (i != j)
                        incoming.Add($"z_{i}_{j}");
                }

                Constraint(output, $"indeg_{j}", string.Join(" + ", incoming), "<=", 1);
            }

            Constraint(output, $"path_{machine}", string.Join(" + ", arcs), "=", operations.Count - 1);
        }

        private void WriteBounds(TextWriter output)
        {
            var instance = _instance;
            long horizon = instance.Horizon;

            output.WriteLine("Bounds");
            for (int operation = 0; operation < instance.OperationCount; operation++)
                output.WriteLine($" {Number(_head[operation])} <= s_{operation} <= {Number(horizon)}");

            for (int job = 0; job < instance.JobCount; job++)
            {
                int last = instance.JobFirst[job + 1] - 1;
                output.WriteLine($" {Number(_head[last] + instance.Duration[last])} <= C_{job} <= {Number(horizon)}");
                output.WriteLine($" 0 <= T_{job} <= {Number(horizon)}");
            }

            long earliestMakespan = 0;
            for (int job = 0; job < instance.JobCount; job++)
            {
                int last = instance.JobFirst[job + 1] - 1;
                earliestMakespan = Math.Max(earliestMakespan, _head[last] + instance.Duration[last]);
            }

            output.WriteLine($" {Number(earliestMakespan)} <= Cmax <= {Number(horizon)}");
            output.WriteLine();
        }

        private void WriteBinaries(TextWriter output)
        {
            if (_binaries.Count == 0)
                return;

            output.WriteLine("Binaries");
            foreach (string name in _binaries)
                output.WriteLine($" {name}");
            output.WriteLine();
        }

        private static void Constraint(TextWriter output, string name, string left, string relation, long right)
        {
            output.WriteLine($" {name}: {left} {relation} {Number(right)}");
        }

        /// <summary>Release plus everything the routing does before this operation.</summary>
        private static long[] Heads(ExactInstance instance)
        {
            var heads = new long[instance.OperationCount];
            for (int job = 0; job < instance.JobCount; job++)
            {
                long head = instance.Release[job];
                for (int operation = instance.JobFirst[job]; operation < instance.JobFirst[job + 1]; operation++)
                {
                    heads[operation] = head;
                    head += instance.Duration[operation];
                }
            }

            return heads;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Number(double value)
        {
            string text = value.ToString("G17", CultureInfo.InvariantCulture);

            // LP format accepts exponents, but not every reader does, and a weight
            // small enough to reach one is not worth the risk.
            return text.Contains('E', StringComparison.Ordinal)
                ? value.ToString("0.####################################", CultureInfo.InvariantCulture)
                : text;
        }
    }
}
