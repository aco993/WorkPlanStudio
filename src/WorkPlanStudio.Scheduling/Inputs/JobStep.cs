namespace WorkPlanStudio.Scheduling;

/// <summary>
/// One operation within a production job: a block of work that must run, in
/// sequence after the previous step, on a single work center.
/// <para>
/// <see cref="DurationSeconds"/> is the whole-lot processing time (setup +
/// per-piece × lot) already reduced to <b>integer seconds</b> by the caller.
/// Keeping time integral makes every schedule bit-for-bit reproducible across
/// the browser (WebAssembly) and CI runtimes — there is no floating-point in
/// the core loop.
/// </para>
/// </summary>
/// <param name="StepNumber">Execution sequence within the job (e.g. 10, 20, 30).</param>
/// <param name="WorkCenterId">The work center this step runs on.</param>
/// <param name="DurationSeconds">Whole-lot processing time in seconds (must be ≥ 0).</param>
/// <param name="SetupFamily">
/// Groups operations that need no change-over between them. Two consecutive
/// steps in the same family cost no setup; switching families costs whatever the
/// work center's <see cref="MachineCapacity.SetupDurations"/> says. Defaults to a
/// single shared family, which means no setup anywhere.
/// </param>
public sealed record JobStep(
    int StepNumber,
    int WorkCenterId,
    long DurationSeconds,
    string SetupFamily = JobStep.DefaultSetupFamily)
{
    /// <summary>The family every step belongs to unless told otherwise.</summary>
    public const string DefaultSetupFamily = "DEFAULT";
}
