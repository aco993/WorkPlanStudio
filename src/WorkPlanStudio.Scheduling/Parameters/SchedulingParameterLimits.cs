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
