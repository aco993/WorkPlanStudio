namespace WorkPlanStudio.Scheduling;

/// <summary>
/// How each job's target completion date (its "meta" / <c>Zieltermin</c>) is
/// assigned before scheduling. These are the classic operations-research
/// due-date assignment rules; the resulting targets drive the due-date dispatch
/// rules and every lateness / tardiness KPI.
/// </summary>
public enum DueDateRule
{
    /// <summary>Use the job's own <see cref="ProductionJob.ExplicitDueSeconds"/> (falls back to a constant allowance when none is set).</summary>
    Explicit,

    /// <summary>TWK — Total Work Content: due = release + factor × total processing.</summary>
    TotalWorkContent,

    /// <summary>NOP — Number of Operations: due = release + secondsPerOp × step count.</summary>
    NumberOfOperations,

    /// <summary>SLK — Equal Slack: due = release + total processing + constant slack.</summary>
    EqualSlack,

    /// <summary>CON — Constant Allowance: due = release + constant allowance.</summary>
    ConstantAllowance
}
