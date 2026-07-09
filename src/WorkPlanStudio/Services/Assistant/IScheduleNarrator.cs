using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Produces a human-readable narration of a (deterministic) schedule explanation.
/// Two implementations sit behind this one interface — a built-in rule-based
/// narrator that always works offline, and an optional bring-your-own-key AI one —
/// so the page, the fallback logic and the tests never depend on a concrete
/// provider. This is the seam the "AI feature" is built on.
/// </summary>
public interface IScheduleNarrator
{
    /// <summary>A short label identifying the provider, shown next to its output.</summary>
    string SourceLabel { get; }

    /// <summary>Narrates <paramref name="explanation"/>. May call the network for AI providers.</summary>
    Task<NarrationResult> NarrateAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default);
}
