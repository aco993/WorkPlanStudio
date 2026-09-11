using System.Diagnostics;
using System.Globalization;
using System.Text;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Scheduling.Exact;

namespace WorkPlanStudio.Scheduling.Testing;

/// <summary>
/// Runs the heuristic and the exact solver over
/// <see cref="OptimalityInstances"/> and reports the gap between them.
/// <para>
/// One definition, used by both the test that pins the numbers and the tool that
/// prints the table, so the documentation and the assertion cannot drift apart —
/// which is exactly how the previous figures came to be attributed to a test that
/// computed something else.
/// </para>
/// </summary>
public static class OptimalityStudy
{
    /// <summary>One instance's result.</summary>
    /// <param name="Name">The instance.</param>
    /// <param name="Features">What it exercises.</param>
    /// <param name="Jobs">Jobs.</param>
    /// <param name="Operations">Operations.</param>
    /// <param name="WorkCentres">Work centers.</param>
    /// <param name="Optimum">The proved optimal penalty.</param>
    /// <param name="DispatchOrderOptimum">
    /// The best penalty reachable by handing the greedy dispatcher a job order —
    /// <see cref="ExhaustiveDispatchOrderSearch"/> over all <c>n!</c> of them. This is
    /// the reference the project used to measure against.
    /// </param>
    /// <param name="Heuristic">What <see cref="SchedulingEngine"/> found.</param>
    /// <param name="Status">Whether the solver proved optimality.</param>
    /// <param name="Nodes">Search nodes the solver opened.</param>
    /// <param name="SolverMilliseconds">Wall clock of the solve; reported, never asserted.</param>
    /// <param name="ExactSchedule">
    /// The optimal schedule itself, so a caller can run it through an independent
    /// feasibility check rather than trusting the number attached to it.
    /// </param>
    public sealed record Row(
        string Name,
        string Features,
        int Jobs,
        int Operations,
        int WorkCentres,
        double Optimum,
        double DispatchOrderOptimum,
        double Heuristic,
        ExactSolutionStatus Status,
        long Nodes,
        double SolverMilliseconds,
        Schedule? ExactSchedule)
    {
        /// <summary>Relative gap of the heuristic to the true optimum; 0 when the optimum is 0.</summary>
        public double Gap => Relative(Heuristic, Optimum);

        /// <summary>
        /// What the dispatch-order model costs: the best job order is still this far
        /// above the optimum, however well the search does.
        /// </summary>
        public double ModelGap => Relative(DispatchOrderOptimum, Optimum);

        /// <summary>
        /// What the search costs: how far the engine lands from the best order it
        /// could have been handed. This is the quantity the old "0.2 % mean gap"
        /// measured, and it is the small one.
        /// </summary>
        public double SearchGap => Relative(Heuristic, DispatchOrderOptimum);

        /// <summary>True when the heuristic found an optimal schedule.</summary>
        public bool HeuristicIsExact => Gap <= 1e-9;

        /// <summary>True when the engine found the best schedule its dispatcher can produce.</summary>
        public bool SearchIsExact => SearchGap <= 1e-9;

        private static double Relative(double found, double reference) =>
            reference <= 1e-12 ? 0 : (found - reference) / reference;
    }

    /// <summary>What the whole set says.</summary>
    /// <param name="Rows">Per-instance results, in instance order.</param>
    public sealed record Summary(IReadOnlyList<Row> Rows)
    {
        /// <summary>Instances whose optimum the solver proved.</summary>
        public int Proved
        {
            get
            {
                int count = 0;
                foreach (var row in Rows)
                {
                    if (row.Status == ExactSolutionStatus.Optimal)
                        count++;
                }

                return count;
            }
        }

        /// <summary>Instances the heuristic solved to optimality.</summary>
        public int SolvedExactly
        {
            get
            {
                int count = 0;
                foreach (var row in Rows)
                {
                    if (row.HeuristicIsExact)
                        count++;
                }

                return count;
            }
        }

        /// <summary>Instances where the engine found the best schedule its dispatcher can produce.</summary>
        public int SearchExact
        {
            get
            {
                int count = 0;
                foreach (var row in Rows)
                {
                    if (row.SearchIsExact)
                        count++;
                }

                return count;
            }
        }

        /// <summary>Mean relative gap of the heuristic to the true optimum.</summary>
        public double MeanGap => Mean(row => row.Gap);

        /// <summary>
        /// Median relative gap. Reported next to the mean because the objective has
        /// a flat hundred-point term per late job, so one instance whose optimum has
        /// no late job and whose heuristic has two drags the mean a long way.
        /// </summary>
        public double MedianGap => Median(row => row.Gap);

        /// <summary>Mean relative gap of the best dispatch order to the true optimum.</summary>
        public double MeanModelGap => Mean(row => row.ModelGap);

        /// <summary>Mean relative gap of the heuristic to the best dispatch order.</summary>
        public double MeanSearchGap => Mean(row => row.SearchGap);

        /// <summary>The worst relative gap in the set.</summary>
        public double WorstGap
        {
            get
            {
                double worst = 0;
                foreach (var row in Rows)
                    worst = Math.Max(worst, row.Gap);

                return worst;
            }
        }

        private double Mean(Func<Row, double> value)
        {
            if (Rows.Count == 0)
                return 0;

            double sum = 0;
            foreach (var row in Rows)
                sum += value(row);

            return sum / Rows.Count;
        }

        private double Median(Func<Row, double> value)
        {
            if (Rows.Count == 0)
                return 0;

            var sorted = new List<double>(Rows.Count);
            foreach (var row in Rows)
                sorted.Add(value(row));

            sorted.Sort();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <summary>Total wall clock of the twenty solves, in milliseconds.</summary>
        public double SolverMilliseconds
        {
            get
            {
                double total = 0;
                foreach (var row in Rows)
                    total += row.SolverMilliseconds;

                return total;
            }
        }
    }

    /// <summary>Runs every instance and returns the rows.</summary>
    /// <param name="options">Solver limits; the defaults prove every instance in the set.</param>
    /// <param name="cancellationToken">Observed by both the solver and the engine.</param>
    public static Summary Run(ExactSolverOptions? options = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<Row>();
        foreach (var instance in OptimalityInstances.All())
        {
            var stopwatch = Stopwatch.StartNew();
            var solution = ExactJobShopSolver.Solve(instance.Context, options, cancellationToken);
            stopwatch.Stop();

            double heuristic = new SchedulingEngine()
                .RunCancellable(instance.Context, cancellationToken)
                .Evaluation.Penalty;

            double dispatchOrder = ExhaustiveDispatchOrderSearch
                .Run(instance.Context, cancellationToken)
                .Result.Evaluation.Penalty;

            rows.Add(new Row(
                instance.Name,
                instance.Features,
                instance.JobCount,
                instance.OperationCount,
                instance.WorkCentreCount,
                solution.Penalty ?? double.NaN,
                dispatchOrder,
                heuristic,
                solution.Status,
                solution.NodesExplored,
                stopwatch.Elapsed.TotalMilliseconds,
                solution.Schedule));
        }

        return new Summary(rows);
    }

    /// <summary>The report as a markdown table, followed by the summary line.</summary>
    public static string ToMarkdown(Summary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var text = new StringBuilder();
        text.AppendLine("| Instance | Jobs | Ops | WC | Optimum | Best order | Engine | Gap | Model gap | Search gap | Nodes | ms |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var row in summary.Rows)
        {
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| `{row.Name}` | {row.Jobs} | {row.Operations} | {row.WorkCentres} | " +
                $"{row.Optimum:F4} | {row.DispatchOrderOptimum:F4} | {row.Heuristic:F4} | " +
                $"{row.Gap:P2} | {row.ModelGap:P2} | {row.SearchGap:P2} | {row.Nodes} | {row.SolverMilliseconds:F1} |"));
        }

        text.AppendLine();
        text.AppendLine("| Instance | Exercises | Solver status |");
        text.AppendLine("| --- | --- | --- |");
        foreach (var row in summary.Rows)
            text.AppendLine($"| `{row.Name}` | {row.Features} | {row.Status} |");

        text.AppendLine();
        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{summary.Proved} of {summary.Rows.Count} instances proved optimal. " +
            $"The engine matches the true optimum on {summary.SolvedExactly} of {summary.Rows.Count}: " +
            $"mean gap {summary.MeanGap:P2}, median {summary.MedianGap:P2}, worst {summary.WorstGap:P2}. " +
            $"It matches the best dispatch order on {summary.SearchExact} of {summary.Rows.Count}: " +
            $"mean search gap {summary.MeanSearchGap:P2}. " +
            $"The best dispatch order is itself {summary.MeanModelGap:P2} above the optimum on average. " +
            $"Total solver time {summary.SolverMilliseconds:F0} ms."));

        return text.ToString();
    }
}
