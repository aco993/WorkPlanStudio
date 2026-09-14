namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// The limits one exact solve runs under, and the propagation it is allowed to
/// spend time on.
/// <para>
/// Job-shop scheduling is NP-hard, so a solver without a limit is a solver that
/// may never return. The limit that matters here is <see cref="NodeLimit"/>,
/// because it is <i>deterministic</i>: the same instance under the same node
/// limit explores the same tree and returns the same answer on every machine, in
/// Release and in Debug, on a desktop runtime and in WebAssembly.
/// <see cref="TimeLimit"/> is the opposite — it is a safety valve for an
/// interactive caller and it makes the answer depend on how busy the machine
/// was. It is off by default and should stay off wherever the answer is compared
/// against a stored one.
/// </para>
/// </summary>
public sealed record ExactSolverOptions
{
    /// <summary>
    /// Search nodes the solver may open before it gives up on proving optimality.
    /// Reaching it downgrades the answer to
    /// <see cref="ExactSolutionStatus.FeasibleWithGap"/> rather than silently
    /// reporting the incumbent as optimal.
    /// </summary>
    public long NodeLimit { get; init; } = 2_000_000;

    /// <summary>
    /// Wall-clock budget, or <c>null</c> (the default) for none. The one setting
    /// on this record that makes a run irreproducible — see the type remarks.
    /// </summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>
    /// Largest instance the solver will accept, counted in operations. Beyond it
    /// the solver refuses the instance instead of returning an answer it cannot
    /// stand behind.
    /// </summary>
    /// <remarks>
    /// The refusal is deliberate. A solver that quietly degrades into a heuristic
    /// on large inputs is worse than one that says no, because the caller keeps
    /// the word "exact" and loses the property.
    /// </remarks>
    public int MaxOperations { get; init; } = 60;

    /// <summary>Largest number of parallel slots, summed over all work centers, the solver will accept.</summary>
    public int MaxSlots { get; init; } = 24;

    /// <summary>
    /// Whether to run disjunctive edge finding (not-first / not-last) and
    /// time-window tightening at each node. On by default: it costs
    /// <c>O(operations³)</c> per work center and pays for itself well before the
    /// sizes this solver is meant for.
    /// </summary>
    public bool UseEdgeFinding { get; init; } = true;

    /// <summary>
    /// Whether to remember states the search has already expanded. Different
    /// orders of placing the same operations on the same slots reach the identical
    /// state, and without this the tree re-explores each of them.
    /// </summary>
    public bool UseStateMemo { get; init; } = true;

    /// <summary>
    /// How many distinct states the memo may hold before it stops growing. A state
    /// costs roughly <c>16 × (jobs + slots)</c> bytes, and the memory is taken on
    /// demand rather than reserved, so a small instance costs kilobytes whatever
    /// this says.
    /// </summary>
    /// <remarks>
    /// This is the single sharpest setting on the record, and it does not degrade
    /// gently. Measured on the study's seven-job instance: 65 536 states leaves the
    /// solver still running after twenty million nodes, and 131 072 finishes in
    /// 535 922 nodes and 0.8 seconds. The default is the first power of two
    /// comfortably past that cliff; it costs about 42 MB on that instance, which is
    /// why <see cref="Interactive"/> does not use it.
    /// </remarks>
    public int StateMemoCapacity { get; init; } = 1 << 18;

    /// <summary>The defaults: two million nodes, no wall-clock limit, full propagation.</summary>
    public static ExactSolverOptions Default { get; } = new();

    /// <summary>
    /// A budget small enough to sit on a browser's render thread: 50 000 nodes
    /// and a memo an order of magnitude smaller than the default.
    /// </summary>
    public static ExactSolverOptions Interactive { get; } = new()
    {
        NodeLimit = 50_000,
        StateMemoCapacity = 1 << 12
    };

    /// <summary>Throws when a limit is outside the range the solver can honour.</summary>
    public void Validate()
    {
        if (NodeLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(NodeLimit), NodeLimit, "The node limit must be at least 1.");
        if (TimeLimit is { } limit && limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeLimit), limit, "The time limit must be positive when set.");
        if (MaxOperations is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(MaxOperations), MaxOperations, "MaxOperations must lie in [1, 4096].");
        if (MaxSlots is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxSlots), MaxSlots, "MaxSlots must lie in [1, 1024].");
        if (StateMemoCapacity is < 0 or > (1 << 24))
            throw new ArgumentOutOfRangeException(nameof(StateMemoCapacity), StateMemoCapacity, "StateMemoCapacity must lie in [0, 16 777 216].");
    }
}
