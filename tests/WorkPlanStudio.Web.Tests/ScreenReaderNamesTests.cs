using System.Text.RegularExpressions;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// What a screen reader announces, held at the source. axe passed every route
/// while the accessibility tree Chromium hands to NVDA and VoiceOver said 51
/// unnamed images (decorative icons without <c>aria-hidden</c>), "plus Add
/// absence", and "Edit, Edit, Edit" down every table - none of it a rule
/// violation, all of it noise for the person listening. These keep it fixed.
/// </summary>
public sealed partial class ScreenReaderNamesTests
{
    private static IEnumerable<(string File, int Line, string Text)> RazorLines() =>
        Directory.EnumerateFiles(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio"), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((text, i) => (Path.GetRelativePath(RepoFiles.Root, f), i + 1, text)));

    [GeneratedRegex(@"<svg\b[^>]*>")]
    private static partial Regex SvgTag();

    [GeneratedRegex(@"class=""icon-btn[^""]*""[^>]*aria-label=""@L\[""Common_(Edit|Delete)""\]""")]
    private static partial Regex BareRowAction();

    [Fact]
    public void Every_icon_is_hidden_or_named()
    {
        var offenders = RazorLines()
            .SelectMany(l => SvgTag().Matches(l.Text).Select(m => (l.File, l.Line, Tag: m.Value)))
            .Where(s => !s.Tag.Contains("aria-hidden=\"true\"", StringComparison.Ordinal)
                     && !(s.Tag.Contains("role=\"img\"", StringComparison.Ordinal) && s.Tag.Contains("aria-label", StringComparison.Ordinal)))
            .Select(s => $"{s.File}:{s.Line}")
            .ToList();

        Assert.True(offenders.Count == 0, "an <svg> a screen reader would announce as an unnamed image:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void The_plus_on_a_new_button_is_not_read_out()
    {
        var offenders = RazorLines()
            .Where(l => l.Text.Contains("class=\"btn-plus\"", StringComparison.Ordinal)
                     && !l.Text.Contains("aria-hidden=\"true\"", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.True(offenders.Count == 0, "\"plus New ...\":\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// An icon button in a table row has no visible text, so its accessible name
    /// is all a listener gets - and "Edit" on its own does not say which row.
    /// </summary>
    [Fact]
    public void A_row_action_says_which_row()
    {
        var offenders = RazorLines()
            .Where(l => BareRowAction().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.True(offenders.Count == 0, "an icon button named only \"Edit\" or \"Delete\":\n" + string.Join('\n', offenders));
    }
}
