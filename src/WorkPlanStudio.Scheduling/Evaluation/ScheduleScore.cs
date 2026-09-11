namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The three numbers the objective is built from, and nothing else.
/// <para>
/// The search compares candidates by <see cref="Penalty"/>, which reads only
/// makespan, total tardiness and the late-job count. Every other KPI on
/// <see cref="ScheduleEvaluation"/> — utilisation, mean flow, the on-time rate —
/// is for the person reading the result, and computing it for a candidate that is
/// about to be discarded is pure cost: utilisation alone was 29 % of the time
/// spent on each of the tens of thousands of candidates a run evaluates, and it
/// grows with the planning horizon rather than with the instance.
/// </para>
/// </summary>
/// <param name="MakespanSeconds">When the last operation finishes.</param>
/// <param name="TotalTardinessSeconds">Sum of every job's tardiness, in seconds.</param>
/// <param name="LateJobCount">How many jobs finish after their target.</param>
public readonly record struct ScheduleScore(
    long MakespanSeconds,
    long TotalTardinessSeconds,
    int LateJobCount)
{
    /// <summary>The weighted objective the search minimises (lower is better).</summary>
    public double Penalty(SchedulingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.MakespanWeight * (MakespanSeconds / 3600.0) +
               parameters.TardinessWeight * (TotalTardinessSeconds / 3600.0) +
               parameters.LatePenalty * LateJobCount;
    }
}
