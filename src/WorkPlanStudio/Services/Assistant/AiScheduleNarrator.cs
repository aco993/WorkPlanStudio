using System.Globalization;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Services;

/// <summary>
/// Optional AI narrator: hands the structured facts to a model behind
/// <see cref="IChatProvider"/> and returns its prose as narration lines.
/// Provider-agnostic — OpenAI-compatible, Anthropic and Gemini all sit behind
/// the same seam. Failures are the caller's concern (<see cref="ScheduleAssistant"/>
/// falls back to the rule-based narrator).
/// </summary>
public sealed class AiScheduleNarrator : IScheduleNarrator
{
    private readonly IChatProvider _provider;
    private readonly string _language;

    public AiScheduleNarrator(IChatProvider provider, string? language = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _language = string.IsNullOrWhiteSpace(language)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language;
    }

    /// <inheritdoc />
    public string SourceLabel => _provider.Label;

    /// <inheritdoc />
    public async Task<NarrationResult> NarrateAsync(ScheduleExplanation explanation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(explanation);

        var content = await _provider.CompleteAsync(
            $"{AssistantPrompt.System} Respond in the language with ISO code '{_language}'.",
            [new ChatTurn(ChatRole.User, AssistantPrompt.BuildFacts(explanation))],
            cancellationToken);

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new NarrationLine(StripBullet(line), FindingTone.Info))
            .ToList();

        return new NarrationResult(lines, NarrationSource.Ai, SourceLabel);
    }

    private static string StripBullet(string line) => line.TrimStart('-', '*', '•', ' ', '\t');
}
