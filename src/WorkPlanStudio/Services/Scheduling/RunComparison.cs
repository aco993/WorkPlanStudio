using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Scheduling;

/// <summary>Whether a change in a figure is good news for a planner.</summary>
public enum Verdict
{
    /// <summary>The figure did not move.</summary>
    Unchanged = 0,

    /// <summary>Moved in the direction a planner wants.</summary>
    Better = 1,

    /// <summary>Moved in the direction a planner does not want.</summary>
    Worse = 2,

    /// <summary>Moved, but neither direction is better in itself - utilisation, for one.</summary>
    Neutral = 3
}

/// <summary>One KPI before and after.</summary>
/// <param name="Kpi">Which KPI, as the resource-key suffix the KPI cards use.</param>
/// <param name="Before">The value in the previous run, in the KPI's own unit (seconds, a 0..1 rate, a count).</param>
/// <param name="After">The value in this run.</param>
/// <param name="Verdict">Whether the move is good news.</param>
public sealed record KpiChange(string Kpi, double Before, double After, Verdict Verdict);

/// <summary>Where two runs differ in what was asked, as opposed to what came out.</summary>
public enum RunDifference
{
    /// <summary>Same parameters, same figures: the search is deterministic, and this is what that looks like.</summary>
    Identical = 0,

    /// <summary>At least one parameter differs; the figures follow from it.</summary>
    ParametersChanged = 1,

    /// <summary>
    /// A parameter differs and not one figure moved. Worth saying rather than
    /// showing four unchanged rows under "the differences follow from what you
    /// changed": on the sample plant, switching EDD to FIFO does exactly this,
    /// because with explicit targets and equal releases the two rules order the
    /// jobs the same way (docs/SCHEDULING.md, section 5).
    /// </summary>
    ParametersChangedNoEffect = 3,

    /// <summary>
    /// Same parameters, different figures. The engine is deterministic, so the
    /// plant data changed between the runs - an order, a work center, a rule -
    /// and saying "you changed nothing" would be false.
    /// </summary>
    DataChanged = 2
}

/// <summary>
/// The previous run beside this one: what moved, in which direction, and why.
/// <para>
/// A planner does not change a parameter to look at one schedule; they change it
/// to see what the change does. The page used to replace one set of KPI cards
/// with another and leave the comparison to memory. Open-source planning tools
/// that offer a Gantt generally stop there too, which is what made this the gap
/// worth closing.
/// </para>
/// </summary>
public static class RunComparison
{
    /// <summary>The four KPIs the cards show, before and after, each with its verdict.</summary>
    public static IReadOnlyList<KpiChange> Kpis(ScheduleKpis before, ScheduleKpis after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return
        [
            new("Makespan", before.MakespanSeconds, after.MakespanSeconds, LowerIsBetter(before.MakespanSeconds, after.MakespanSeconds)),
            new("OnTime", before.JobCount - before.LateJobCount, after.JobCount - after.LateJobCount,
                HigherIsBetter(before.JobCount - before.LateJobCount, after.JobCount - after.LateJobCount)),
            new("Tardiness", before.TotalTardinessSeconds, after.TotalTardinessSeconds, LowerIsBetter(before.TotalTardinessSeconds, after.TotalTardinessSeconds)),
            // Higher utilisation is not better in itself: the same machines busier
            // can mean a tighter plan or a longer queue. Reported, not judged.
            new("Utilization", before.AverageUtilization, after.AverageUtilization,
                Same(before.AverageUtilization, after.AverageUtilization) ? Verdict.Unchanged : Verdict.Neutral)
        ];
    }

    /// <summary>The names of the parameters that differ, in the order the form shows them.</summary>
    public static IReadOnlyList<string> ChangedParameters(SchedulingParameters before, SchedulingParameters after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changed = new List<string>();
        void Check(bool differs, string name)
        {
            if (differs)
                changed.Add(name);
        }

        Check(before.DispatchRule != after.DispatchRule, nameof(SchedulingParameters.DispatchRule));
        Check(before.LocalSearchAcceptance != after.LocalSearchAcceptance, nameof(SchedulingParameters.LocalSearchAcceptance));
        Check(before.DueDateRule != after.DueDateRule, nameof(SchedulingParameters.DueDateRule));
        Check(!Same(before.TwkFlowFactor, after.TwkFlowFactor), nameof(SchedulingParameters.TwkFlowFactor));
        Check(before.NopSecondsPerOp != after.NopSecondsPerOp, nameof(SchedulingParameters.NopSecondsPerOp));
        Check(before.SlackSeconds != after.SlackSeconds, nameof(SchedulingParameters.SlackSeconds));
        Check(before.ConstantAllowanceSeconds != after.ConstantAllowanceSeconds, nameof(SchedulingParameters.ConstantAllowanceSeconds));
        Check(before.MultiStartRuns != after.MultiStartRuns, nameof(SchedulingParameters.MultiStartRuns));
        Check(before.LocalSearchMaxSteps != after.LocalSearchMaxSteps, nameof(SchedulingParameters.LocalSearchMaxSteps));
        Check(before.Seed != after.Seed, nameof(SchedulingParameters.Seed));
        return changed;
    }

    /// <summary>Why the two runs differ, or that they do not.</summary>
    public static RunDifference Difference(
        SchedulingParameters beforeParameters, ScheduleKpis before,
        SchedulingParameters afterParameters, ScheduleKpis after)
    {
        var unchanged = Kpis(before, after).All(k => k.Verdict == Verdict.Unchanged);
        if (ChangedParameters(beforeParameters, afterParameters).Count > 0)
            return unchanged ? RunDifference.ParametersChangedNoEffect : RunDifference.ParametersChanged;
        return unchanged ? RunDifference.Identical : RunDifference.DataChanged;
    }

    private static Verdict LowerIsBetter(double before, double after) =>
        Same(before, after) ? Verdict.Unchanged : after < before ? Verdict.Better : Verdict.Worse;

    private static Verdict HigherIsBetter(double before, double after) =>
        Same(before, after) ? Verdict.Unchanged : after > before ? Verdict.Better : Verdict.Worse;

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;
}
