namespace WorkPlanStudio.Scheduling;

/// <summary>Central deterministic safety limits for browser and library callers.</summary>
public static class SchedulingParameterLimits
{
    /// <summary>Inclusive multi-start lower bound.</summary>
    public const int MinMultiStartRuns = 1;
    /// <summary>Inclusive multi-start upper bound.</summary>
    public const int MaxMultiStartRuns = 64;
    /// <summary>Inclusive local-search lower bound.</summary>
    public const int MinLocalSearchSteps = 0;
    /// <summary>Inclusive local-search upper bound.</summary>
    public const int MaxLocalSearchSteps = 20_000;

    /// <summary>
    /// Inclusive bound on <c>MultiStartRuns × LocalSearchMaxSteps</c> — the number
    /// of candidate schedules a run may actually build.
    /// </summary>
    /// <remarks>
    /// Bounding the two factors separately bounds neither the work nor the memory,
    /// because the local-search budget is spent <i>per restart</i>: the pair
    /// (64, 20 000) is 1.28 million dispatches, which no interactive caller wants
    /// and a 32-bit WebAssembly heap cannot hold. 200 000 is the largest product
    /// measured to stay near a second at the 100-job / 600-operation size on a
    /// desktop runtime; a browser is slower, so treat it as a ceiling rather than
    /// a recommendation.
    /// </remarks>
    public const int MaxTotalEvaluations = 200_000;

    /// <summary>Inclusive display-day lower bound.</summary>
    public const int MinMinutesPerWorkingDay = 1;
    /// <summary>Inclusive display-day upper bound.</summary>
    public const int MaxMinutesPerWorkingDay = 1_440;
    /// <summary>Inclusive TWK factor lower bound.</summary>
    public const double MinTwkFlowFactor = 0.1;
    /// <summary>Inclusive TWK factor upper bound.</summary>
    public const double MaxTwkFlowFactor = 100;
    /// <summary>Maximum ten-year relative target-date allowance in seconds.</summary>
    public const long MaxDueDateSeconds = 10L * 365 * 24 * 60 * 60;

    /// <summary>
    /// Longest single operation, release offset, explicit target date, calendar
    /// period or change-over the engine accepts — ten years, the same bound the
    /// target-date allowances use.
    /// </summary>
    /// <remarks>
    /// A per-value bound is what keeps the shared machine clock inside
    /// <see cref="long"/>. Without it a single <c>long.MaxValue</c> step wraps the
    /// timeline negative, and a negative total tardiness scores <i>better</i> than
    /// every feasible schedule — so the optimiser chases the overflow instead of
    /// rejecting it.
    /// </remarks>
    public const long MaxStepDurationSeconds = MaxDueDateSeconds;

    /// <summary>
    /// Longest planning horizon the engine will schedule into, in seconds
    /// (about 31 700 years).
    /// </summary>
    /// <remarks>
    /// The total work of an instance is checked against this at context
    /// construction. Every clock value the dispatcher produces is bounded by the
    /// total work plus a few calendar periods, and every tardiness sum by the job
    /// count times that — so this bound, not <c>checked</c> arithmetic alone, is
    /// what makes the timeline provably overflow-free.
    /// </remarks>
    public const long MaxHorizonSeconds = 1_000_000_000_000L;

    /// <summary>Throws when any parameter is outside the supported deterministic range.</summary>
    public static void Validate(SchedulingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Range(parameters.MultiStartRuns, MinMultiStartRuns, MaxMultiStartRuns, nameof(parameters.MultiStartRuns));
        Range(parameters.LocalSearchMaxSteps, MinLocalSearchSteps, MaxLocalSearchSteps, nameof(parameters.LocalSearchMaxSteps));
        Range(parameters.MinutesPerWorkingDay, MinMinutesPerWorkingDay, MaxMinutesPerWorkingDay, nameof(parameters.MinutesPerWorkingDay));
        Range(parameters.NopSecondsPerOp, 0, MaxDueDateSeconds, nameof(parameters.NopSecondsPerOp));
        Range(parameters.SlackSeconds, 0, MaxDueDateSeconds, nameof(parameters.SlackSeconds));
        Range(parameters.ConstantAllowanceSeconds, 0, MaxDueDateSeconds, nameof(parameters.ConstantAllowanceSeconds));

        // The product, not just the factors: this is the number that costs.
        long total = (long)parameters.MultiStartRuns * parameters.LocalSearchMaxSteps;
        if (total > MaxTotalEvaluations)
            throw new ArgumentOutOfRangeException(
                nameof(parameters.LocalSearchMaxSteps),
                total,
                $"MultiStartRuns × LocalSearchMaxSteps must not exceed {MaxTotalEvaluations}.");

        if (!double.IsFinite(parameters.TwkFlowFactor) ||
            parameters.TwkFlowFactor < MinTwkFlowFactor ||
            parameters.TwkFlowFactor > MaxTwkFlowFactor)
            throw new ArgumentOutOfRangeException(nameof(parameters.TwkFlowFactor));

        NonNegativeFinite(parameters.MakespanWeight, nameof(parameters.MakespanWeight));
        NonNegativeFinite(parameters.TardinessWeight, nameof(parameters.TardinessWeight));
        NonNegativeFinite(parameters.LatePenalty, nameof(parameters.LatePenalty));
        if (!Enum.IsDefined(parameters.DispatchRule))
            throw new ArgumentOutOfRangeException(nameof(parameters.DispatchRule));
        if (!Enum.IsDefined(parameters.DueDateRule))
            throw new ArgumentOutOfRangeException(nameof(parameters.DueDateRule));
        if (!Enum.IsDefined(parameters.LocalSearchAcceptance))
            throw new ArgumentOutOfRangeException(nameof(parameters.LocalSearchAcceptance));
    }

    private static void Range(long value, long minimum, long maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be between {minimum} and {maximum}.");
    }

    private static void NonNegativeFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
