using System.Globalization;
using WorkPlanStudio.Scheduling.Exact;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The LP model is the part of this work a reviewer can check without running any
/// of it, so it is tested by reading it back rather than by eyeballing it: the
/// file is parsed, every variable is required to be declared and bounded, the
/// objective is required to be the engine's penalty term for term, and — the test
/// that would actually catch a wrong formulation — the schedule the
/// branch-and-bound proved optimal is substituted into every constraint and has to
/// satisfy all of them at exactly the objective value the solver reported.
/// <para>
/// A model that is merely well formed is worth little. A model that the known
/// optimum satisfies, at the known objective, is a model that encodes this
/// problem and not a neighbouring one.
/// </para>
/// </summary>
public class ExactMilpWriterTests
{
    [Fact]
    public void The_model_is_well_formed_and_every_variable_is_declared_and_bounded()
    {
        var context = ReleasedInstance();
        var model = LpModel.Parse(MilpModelWriter.Write(context));

        Assert.NotEmpty(model.Objective);
        Assert.NotEmpty(model.Constraints);
        Assert.Equal(model.Constraints.Select(c => c.Name).Distinct().Count(), model.Constraints.Count);

        foreach (string name in model.Objective.Keys.Concat(model.Constraints.SelectMany(c => c.Terms.Keys)).Distinct())
        {
            bool declared = model.Bounds.ContainsKey(name) || model.Binaries.Contains(name);
            Assert.True(declared, $"variable {name} is used but never declared");
        }

        foreach (var (name, bound) in model.Bounds)
        {
            Assert.True(double.IsFinite(bound.Lower), $"{name} has no finite lower bound");
            Assert.True(double.IsFinite(bound.Upper), $"{name} has no finite upper bound");
            Assert.True(bound.Lower <= bound.Upper, $"{name} has an empty range [{bound.Lower}, {bound.Upper}]");
        }

        // A binary is [0,1] by its section and must not also carry a Bounds row.
        foreach (string binary in model.Binaries)
            Assert.DoesNotContain(binary, model.Bounds.Keys);
    }

    [Fact]
    public void The_objective_is_the_engine_penalty_term_for_term()
    {
        var context = ReleasedInstance();
        var parameters = context.Parameters;
        var model = LpModel.Parse(MilpModelWriter.Write(context));

        Assert.Equal(parameters.MakespanWeight / 3600.0, model.Objective["Cmax"], 12);
        for (int job = 0; job < context.Jobs.Count; job++)
        {
            Assert.Equal(parameters.TardinessWeight / 3600.0, model.Objective[$"T_{job}"], 12);
            Assert.Equal(parameters.LatePenalty, model.Objective[$"U_{job}"], 12);
        }

        // Nothing else may appear in the objective: a stray term is a different
        // problem wearing the same name.
        Assert.Equal(1 + 2 * context.Jobs.Count, model.Objective.Count);
    }

    /// <summary>
    /// Big-M is the instance horizon, not a number somebody liked. Doubling every
    /// duration has to double it; a magic constant would not move.
    /// </summary>
    [Fact]
    public void Big_m_follows_the_instance_rather_than_a_constant()
    {
        var small = Context(
            Weights(), [Machine(1)],
            Job(1, Step(10, 1, 1_000)),
            Job(2, Step(10, 1, 2_000)));

        var large = Context(
            Weights(), [Machine(1)],
            Job(1, Step(10, 1, 2_000)),
            Job(2, Step(10, 1, 4_000)));

        Assert.Equal(3_001, MilpModelWriter.BigM(small));
        Assert.Equal(6_001, MilpModelWriter.BigM(large));
        Assert.Contains($"Big-M = {MilpModelWriter.BigM(small)}", MilpModelWriter.Write(small), StringComparison.Ordinal);
    }

    /// <summary>
    /// The check that matters: the proved optimum, written into the model, has to
    /// be a feasible point at exactly the objective the solver reported. A
    /// formulation that is too tight fails a constraint here; one that is too loose
    /// is caught by the bound tests in <see cref="ExactSolverTests"/>, which would
    /// find a schedule the model admits and the shop does not.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("releases")]
    [InlineData("parallel")]
    [InlineData("setups")]
    [InlineData("tight-dates")]
    public void The_proved_optimum_satisfies_every_constraint_at_the_reported_objective(string shape)
    {
        var context = Shape(shape);
        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);
        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);

        var model = LpModel.Parse(MilpModelWriter.Write(context));
        var point = Assignment(context, solution.Schedule!);

        foreach (var constraint in model.Constraints)
        {
            double left = constraint.Terms.Sum(term => term.Value * point[term.Key]);
            bool satisfied = constraint.Relation switch
            {
                ">=" => left >= constraint.Right - 1e-6,
                "<=" => left <= constraint.Right + 1e-6,
                _ => Math.Abs(left - constraint.Right) <= 1e-6
            };

            Assert.True(satisfied,
                $"{constraint.Name}: {left:F3} {constraint.Relation} {constraint.Right:F3} is false for the proved optimum");
        }

        foreach (var (name, bound) in model.Bounds)
        {
            Assert.True(point[name] >= bound.Lower - 1e-6, $"{name} = {point[name]} is below its bound {bound.Lower}");
            Assert.True(point[name] <= bound.Upper + 1e-6, $"{name} = {point[name]} is above its bound {bound.Upper}");
        }

        double objective = model.Objective.Sum(term => term.Value * point[term.Key]);
        Assert.Equal(solution.OptimalPenalty, objective, 9);
    }

    /// <summary>
    /// The other half of the check, and the one a reviewer's solver would do: solve
    /// the emitted model and compare. On an instance small enough to enumerate every
    /// binary assignment, the continuous part collapses to a system of difference
    /// constraints whose componentwise-smallest solution is the optimum for that
    /// assignment — so the model's true optimum can be computed here, with no solver
    /// and no shared code, and held against the branch-and-bound.
    /// <para>
    /// The previous test proves the model is not too tight. This one proves it is
    /// not too loose: a formulation that admitted an infeasible schedule would
    /// report a smaller optimum than the shop can achieve, and a reviewer running
    /// HiGHS would conclude the solver was wrong rather than the model.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("tiny")]
    [InlineData("tiny-setups")]
    [InlineData("tiny-parallel")]
    [InlineData("plain")]
    [InlineData("releases")]
    [InlineData("tight-dates")]
    public void The_models_own_optimum_is_the_one_the_branch_and_bound_proved(string shape)
    {
        var context = Shape(shape);
        var solution = ExactJobShopSolver.Solve(context, cancellationToken: Ct);
        Assert.Equal(ExactSolutionStatus.Optimal, solution.Status);

        var model = LpModel.Parse(MilpModelWriter.Write(context));
        double? best = model.EnumerateOptimum();

        Assert.NotNull(best);
        Assert.Equal(solution.OptimalPenalty, best!.Value, 6);
    }

    [Fact]
    public void A_calendar_is_refused_with_the_reason_rather_than_written_wrongly()
    {
        var machine = new MachineCapacity(1, "WC-1")
        {
            AvailabilityWindows = [new CapacityWindow(6 * 3600, 14 * 3600)],
            CalendarPeriodSeconds = 24 * 3600
        };

        var context = Context(Weights(), [machine], Job(1, Step(10, 1, 3_600)));

        Assert.False(MilpModelWriter.CanWrite(context, out string reason));
        Assert.Contains("time-indexed", reason, StringComparison.Ordinal);
        var error = Assert.Throws<NotSupportedException>(() => MilpModelWriter.Write(context));
        Assert.Contains("calendar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_over_matrix_on_a_work_centre_with_several_slots_is_refused()
    {
        var machine = new MachineCapacity(1, "WC-1", ParallelCapacity: 2)
        {
            SetupDurations = [new SetupDuration("A", "B", 600)]
        };

        var context = Context(
            Weights(), [machine],
            Job(1, new JobStep(10, 1, 1_200, "A")),
            Job(2, new JobStep(10, 1, 1_200, "B")));

        Assert.False(MilpModelWriter.CanWrite(context, out string reason));
        Assert.Contains("parallel slots", reason, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => MilpModelWriter.Write(context));
    }

    // ----- fixtures -----

    private static SchedulingParameters Weights() => new()
    {
        DueDateRule = DueDateRule.TotalWorkContent,
        TwkFlowFactor = 1.5,
        MakespanWeight = 1.0,
        TardinessWeight = 10.0,
        LatePenalty = 100.0
    };

    private static SchedulingContext ReleasedInstance() => Shape("releases");

    private static SchedulingContext Shape(string shape) => shape switch
    {
        "plain" => Context(
            Weights(), [Machine(1), Machine(2)],
            Job(1, Step(10, 1, 1_200), Step(20, 2, 900)),
            Job(2, Step(10, 2, 600), Step(20, 1, 1_500)),
            Job(3, Step(10, 1, 300), Step(20, 2, 1_800))),

        "releases" => Context(
            Weights(), [Machine(1), Machine(2)],
            Released(1, 0, Step(10, 1, 1_200), Step(20, 2, 900)),
            Released(2, 1_800, Step(10, 2, 600), Step(20, 1, 1_500)),
            Released(3, 3_600, Step(10, 1, 300), Step(20, 2, 1_800))),

        "parallel" => Context(
            Weights(), [Machine(1, capacity: 2), Machine(2)],
            Job(1, Step(10, 1, 1_200), Step(20, 2, 900)),
            Job(2, Step(10, 1, 600), Step(20, 2, 1_500)),
            Job(3, Step(10, 1, 900), Step(20, 2, 600))),

        "setups" => new SchedulingContext(
            [
                Job(1, new JobStep(10, 1, 1_200, "ALU"), new JobStep(20, 2, 600)),
                Job(2, new JobStep(10, 1, 900, "STEEL"), new JobStep(20, 2, 900)),
                Job(3, new JobStep(10, 1, 600, "ALU"), new JobStep(20, 2, 300))
            ],
            [
                new MachineCapacity(1, "WC-1")
                {
                    SetupDurations = [new SetupDuration("ALU", "STEEL", 1_800), new SetupDuration("STEEL", "ALU", 1_200)]
                },
                Machine(2)
            ],
            Weights()),

        "tiny" => Context(
            Weights(), [Machine(1), Machine(2)],
            Job(1, Step(10, 1, 1_200), Step(20, 2, 900)),
            Job(2, Step(10, 2, 600), Step(20, 1, 1_500))),

        "tiny-setups" => new SchedulingContext(
            [
                Job(1, new JobStep(10, 1, 1_200, "ALU")),
                Job(2, new JobStep(10, 1, 900, "STEEL")),
                Job(3, new JobStep(10, 1, 600, "ALU"))
            ],
            [
                new MachineCapacity(1, "WC-1")
                {
                    SetupDurations = [new SetupDuration("ALU", "STEEL", 1_800), new SetupDuration("STEEL", "ALU", 1_200)]
                }
            ],
            Weights()),

        "tiny-parallel" => Context(
            Weights(), [Machine(1, capacity: 2)],
            Job(1, Step(10, 1, 1_200)),
            Job(2, Step(10, 1, 900)),
            Job(3, Step(10, 1, 600))),

        _ => Context(
            new SchedulingParameters
            {
                DueDateRule = DueDateRule.Explicit,
                MakespanWeight = 1.0,
                TardinessWeight = 10.0,
                LatePenalty = 100.0
            },
            [Machine(1), Machine(2)],
            DueAt(1, 2_000, Step(10, 1, 1_200), Step(20, 2, 900)),
            DueAt(2, 2_400, Step(10, 2, 600), Step(20, 1, 1_500)),
            DueAt(3, 9_000, Step(10, 1, 300), Step(20, 2, 1_800)))
    };

    /// <summary>
    /// The proved schedule as a point in the model's variables.
    /// </summary>
    /// <remarks>
    /// <c>s_o</c> is the start of <i>processing</i>, while the engine's
    /// <see cref="ScheduledOperation.StartSeconds"/> is the start of the block that
    /// begins with the change-over. The two differ by exactly the change-over, and
    /// getting that wrong is how a model quietly stops describing the shop.
    /// </remarks>
    private static Dictionary<string, double> Assignment(SchedulingContext context, Schedule schedule)
    {
        var point = new Dictionary<string, double>(StringComparer.Ordinal);
        var indexOf = new Dictionary<(int Job, int Step), int>();
        var operationOfIndex = new Dictionary<int, ScheduledOperation>();

        int cursor = 0;
        for (int job = 0; job < context.Jobs.Count; job++)
        {
            foreach (var step in context.Jobs[job].Steps)
                indexOf[(context.Jobs[job].Id, step.StepNumber)] = cursor++;
        }

        foreach (var operation in schedule.Operations)
        {
            int index = indexOf[(operation.JobId, operation.StepNumber)];
            operationOfIndex[index] = operation;
            point[$"s_{index}"] = operation.StartSeconds + operation.SetupSeconds;
        }

        double makespan = 0;
        for (int job = 0; job < context.Jobs.Count; job++)
        {
            var row = schedule.Jobs.Single(j => j.JobId == context.Jobs[job].Id);
            point[$"C_{job}"] = row.CompletionSeconds;
            point[$"T_{job}"] = row.TardinessSeconds;
            point[$"U_{job}"] = row.IsLate ? 1 : 0;
            makespan = Math.Max(makespan, row.CompletionSeconds);
        }

        point["Cmax"] = makespan;

        // Sequencing, slot assignment and the change-over arcs, read off the
        // schedule the solver actually produced.
        var byWorkCentre = operationOfIndex
            .GroupBy(pair => pair.Value.WorkCenterId)
            .OrderBy(group => group.Key);

        foreach (var group in byWorkCentre)
        {
            var members = group.OrderBy(pair => pair.Key).ToList();
            int capacity = context.CapacityOf(group.Key);

            foreach (var (index, operation) in members)
            {
                if (capacity > 1)
                {
                    for (int slot = 0; slot < capacity; slot++)
                        point[$"y_{index}_{slot}"] = operation.SlotIndex == slot ? 1 : 0;
                }
            }

            for (int a = 0; a < members.Count; a++)
            {
                for (int b = a + 1; b < members.Count; b++)
                {
                    var (i, left) = members[a];
                    var (j, right) = members[b];
                    bool before = left.StartSeconds < right.StartSeconds ||
                                  (left.StartSeconds == right.StartSeconds && i < j);
                    point[$"x_{i}_{j}"] = before ? 1 : 0;
                }
            }

            if (context.Machines[group.Key].SetupDurations.Count == 0)
                continue;

            var sequence = members
                .OrderBy(pair => pair.Value.StartSeconds)
                .ThenBy(pair => pair.Key)
                .Select(pair => pair.Key)
                .ToList();

            foreach (int i in sequence)
            {
                foreach (int j in sequence)
                {
                    if (i != j)
                        point[$"z_{i}_{j}"] = 0;
                }
            }

            for (int position = 0; position + 1 < sequence.Count; position++)
                point[$"z_{sequence[position]}_{sequence[position + 1]}"] = 1;
        }

        return point;
    }

    /// <summary>A reader for the subset of CPLEX LP format this writer emits.</summary>
    private sealed record LpModel(
        Dictionary<string, double> Objective,
        List<LpConstraint> Constraints,
        Dictionary<string, (double Lower, double Upper)> Bounds,
        HashSet<string> Binaries)
    {
        /// <summary>
        /// The model's optimum, by trying every assignment of its binaries and
        /// solving the difference-constraint system each one leaves behind. Only
        /// usable on instances with a handful of binaries, which is the point: it
        /// is a reference, not a solver.
        /// </summary>
        public double? EnumerateOptimum()
        {
            var binaries = Binaries.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.True(binaries.Length <= 16, $"{binaries.Length} binaries is too many to enumerate");

            var continuous = Bounds.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            double? best = null;

            for (int assignment = 0; assignment < 1 << binaries.Length; assignment++)
            {
                var point = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int bit = 0; bit < binaries.Length; bit++)
                    point[binaries[bit]] = (assignment >> bit) & 1;

                foreach (string name in continuous)
                    point[name] = Bounds[name].Lower;

                if (!Settle(point, continuous.Length))
                    continue;

                double objective = Objective.Sum(term => term.Value * point[term.Key]);
                if (best is null || objective < best)
                    best = objective;
            }

            return best;
        }

        /// <summary>
        /// Pushes the continuous variables up until every <c>&gt;=</c> row holds,
        /// then checks the rows that only a too-large value could break. Returns
        /// false when the assignment admits nothing.
        /// </summary>
        private bool Settle(Dictionary<string, double> point, int continuousCount)
        {
            for (int round = 0; round <= continuousCount * Constraints.Count + 2; round++)
            {
                bool changed = false;
                foreach (var constraint in Constraints)
                {
                    foreach (string direction in constraint.Relation == "=" ? [">=", "<="] : new[] { constraint.Relation })
                    {
                        double sign = direction == ">=" ? 1 : -1;
                        double right = sign * constraint.Right;

                        string? target = null;
                        double rest = 0;
                        bool shaped = true;
                        foreach (var (name, coefficient) in constraint.Terms)
                        {
                            double scaled = sign * coefficient;
                            if (Binaries.Contains(name))
                            {
                                rest += scaled * point[name];
                                continue;
                            }

                            if (scaled > 0 && target is null)
                                target = name;
                            else if (scaled < 0)
                                rest += scaled * point[name];
                            else
                                shaped = false;
                        }

                        if (!shaped || target is null)
                            continue;

                        double required = right - rest;
                        if (required > point[target] + 1e-9)
                        {
                            point[target] = required;
                            changed = true;
                        }
                    }
                }

                if (!changed)
                    return Holds(point);
            }

            return false;   // a positive cycle: this assignment has no schedule
        }

        private bool Holds(Dictionary<string, double> point)
        {
            foreach (var (name, bound) in Bounds)
            {
                if (point[name] < bound.Lower - 1e-6 || point[name] > bound.Upper + 1e-6)
                    return false;
            }

            foreach (var constraint in Constraints)
            {
                double left = constraint.Terms.Sum(term => term.Value * point[term.Key]);
                bool satisfied = constraint.Relation switch
                {
                    ">=" => left >= constraint.Right - 1e-6,
                    "<=" => left <= constraint.Right + 1e-6,
                    _ => Math.Abs(left - constraint.Right) <= 1e-6
                };

                if (!satisfied)
                    return false;
            }

            return true;
        }

        public static LpModel Parse(string text)
        {
            var objective = new Dictionary<string, double>(StringComparer.Ordinal);
            var constraints = new List<LpConstraint>();
            var bounds = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
            var binaries = new HashSet<string>(StringComparer.Ordinal);
            var sections = new List<string>();

            string section = "none";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('\\'))
                    continue;

                string keyword = line.ToUpperInvariant();
                if (keyword is "MINIMIZE" or "SUBJECT TO" or "BOUNDS" or "BINARIES" or "END")
                {
                    section = keyword;
                    sections.Add(keyword);
                    continue;
                }

                switch (section)
                {
                    case "MINIMIZE":
                        foreach (var (name, coefficient) in Terms(line[(line.IndexOf(':') + 1)..]))
                            objective[name] = coefficient;
                        break;

                    case "SUBJECT TO":
                        constraints.Add(ParseConstraint(line));
                        break;

                    case "BOUNDS":
                        var parts = line.Split("<=", StringSplitOptions.TrimEntries);
                        Assert.Equal(3, parts.Length);
                        bounds[parts[1]] = (Value(parts[0]), Value(parts[2]));
                        break;

                    case "BINARIES":
                        binaries.Add(line);
                        break;
                }
            }

            Assert.Equal(["MINIMIZE", "SUBJECT TO", "BOUNDS", "BINARIES", "END"], sections);
            return new LpModel(objective, constraints, bounds, binaries);
        }

        private static LpConstraint ParseConstraint(string line)
        {
            int colon = line.IndexOf(':');
            string name = line[..colon].Trim();
            string body = line[(colon + 1)..];

            string relation = body.Contains(">=", StringComparison.Ordinal) ? ">="
                : body.Contains("<=", StringComparison.Ordinal) ? "<="
                : "=";

            int at = body.IndexOf(relation, StringComparison.Ordinal);
            var terms = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (variable, coefficient) in Terms(body[..at]))
                terms[variable] = terms.GetValueOrDefault(variable) + coefficient;

            return new LpConstraint(name, terms, relation, Value(body[(at + relation.Length)..]));
        }

        private static IEnumerable<(string Name, double Coefficient)> Terms(string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double sign = 1;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token == "+") { sign = 1; continue; }
                if (token == "-") { sign = -1; continue; }

                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double coefficient))
                {
                    Assert.True(i + 1 < tokens.Length, $"coefficient {token} has no variable");
                    yield return (tokens[++i], sign * coefficient);
                }
                else
                {
                    yield return (token, sign);
                }

                sign = 1;
            }
        }

        private static double Value(string text) =>
            double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private sealed record LpConstraint(string Name, Dictionary<string, double> Terms, string Relation, double Right);
}
