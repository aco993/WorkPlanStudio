using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>Which kind of narrator produced a narration.</summary>
public enum NarrationSource
{
    /// <summary>The built-in, deterministic, offline narrator.</summary>
    RuleBased,

    /// <summary>An external, bring-your-own-key AI provider.</summary>
    Ai
}

/// <summary>One line of narration, tagged with a tone so the UI can colour it.</summary>
/// <param name="Text">The already-localized display text.</param>
/// <param name="Tone">Good / Info / Warning — reused from the engine's finding tone.</param>
public sealed record NarrationLine(string Text, FindingTone Tone);

/// <summary>The narrated explanation shown to the user.</summary>
/// <param name="Lines">The explanation as display lines.</param>
/// <param name="Source">Whether a rule-based or an AI narrator produced it.</param>
/// <param name="SourceLabel">Human label for the source (e.g. "Rule-based" or "openai.com · gpt-4o-mini").</param>
/// <param name="Note">Optional note — e.g. explaining that an AI call fell back to the rule-based text.</param>
public sealed record NarrationResult(
    IReadOnlyList<NarrationLine> Lines,
    NarrationSource Source,
    string SourceLabel,
    string? Note = null);
