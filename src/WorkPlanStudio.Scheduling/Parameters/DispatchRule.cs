namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The priority rule that decides, when several jobs compete for the same work
/// center, which one is sequenced first. Each rule turns into an initial job
/// priority order that the dispatch scheduler consumes; local search then refines
/// that order.
/// </summary>
public enum DispatchRule
{
    /// <summary>First in, first out — by release time, then id.</summary>
    Fifo,

    /// <summary>Shortest Processing Time — least total work first (minimises mean flow time).</summary>
    ShortestProcessingTime,

    /// <summary>Longest Processing Time — most total work first.</summary>
    LongestProcessingTime,

    /// <summary>Earliest Due Date — most urgent target first (minimises maximum lateness).</summary>
    EarliestDueDate,

    /// <summary>Critical Ratio — by due ÷ remaining work, evaluated at the horizon start.</summary>
    CriticalRatio,

    /// <summary>Weighted Shortest Processing Time — by processing ÷ weight (favours important, short jobs).</summary>
    WeightedShortestProcessingTime
}
