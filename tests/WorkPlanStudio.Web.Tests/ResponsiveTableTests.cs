using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Resources;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// <c>.card { overflow: hidden }</c> clipped every data table instead of scrolling
/// it. Because the card clipped, the document never reported horizontal overflow —
/// so the columns were not scrolled off the screen, they were gone, and the Actions
/// column is the last one on every list in this app. These tests hold the two halves
/// of the fix in place: a scroll container in the markup, and a stylesheet that no
/// longer clips a card holding a table.
/// </summary>
public sealed class ResponsiveTableTests : AppBunitContext
{
    private static string Css => File.ReadAllText(Path.Join(RepoFiles.AppWwwroot, "css", "app.css"));

    private static IEnumerable<string> RazorFiles =>
        Directory.EnumerateFiles(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio"), "*.razor", SearchOption.AllDirectories);

    [Fact]
    public void The_scroll_container_is_a_named_region_a_keyboard_can_reach()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());

        var cut = Render<TableScroll>(parameters => parameters
            .Add(component => component.Label, "Work centers")
            .AddChildContent("<table class=\"data-table\"><tbody><tr><td>x</td></tr></tbody></table>"));

        var region = cut.Find(".table-scroll");
        Assert.Equal("region", region.GetAttribute("role"));
        Assert.Equal("Work centers", region.GetAttribute("aria-label"));
        // A region that only a pointing device can scroll is the same defect wearing
        // a scrollbar, so it is a tab stop of its own.
        Assert.Equal("0", region.GetAttribute("tabindex"));
    }

    /// <summary>
    /// Every table in the app has to be reachable, whether or not the page that owns
    /// it has adopted <see cref="TableScroll"/> yet: a table wrapped in the component,
    /// or sitting directly inside a card that the stylesheet lets scroll.
    /// </summary>
    [Fact]
    public void Every_data_table_either_sits_in_a_scroll_container_or_directly_inside_a_card()
    {
        var offenders = new List<string>();

        foreach (var file in RazorFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"<table[^>]*class=""[^""]*\bdata-table\b"))
                    continue;
                if (!ScrollableAncestor(lines, i))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "data tables with nothing to scroll them: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Walks back to the enclosing block. Either the table is wrapped in the
    /// component, or the enclosing card is one the stylesheet lets scroll; anything
    /// else is a table that a phone clips.
    /// </summary>
    private static bool ScrollableAncestor(string[] lines, int index)
    {
        for (var i = index - 1; i >= 0 && i >= index - 25; i--)
        {
            if (lines[i].Contains("<TableScroll", StringComparison.Ordinal))
                return true;
            if (lines[i].Contains("class=\"card no-pad\"", StringComparison.Ordinal))
                return true;
            // A text alternative is never the thing being clipped — and it is wrapped
            // in a div precisely so that it cannot widen the document either.
            if (lines[i].Contains("class=\"sr-only\"", StringComparison.Ordinal))
                return true;
            if (Regex.IsMatch(lines[i], @"<(div|section) class=""card"""))
                return false;
        }
        return false;
    }

    /// <summary>
    /// A table box grows to its min-content width whatever <c>width</c> says, and
    /// <c>overflow: hidden</c> on the table itself does not stop it. Putting
    /// <c>sr-only</c> straight on a <c>&lt;table&gt;</c> therefore hides it from sight
    /// and still pushes the document into a horizontal scroll — measured at 1,585 px
    /// on a 640 px viewport before the wrapper went in. Only a block wrapper clips.
    /// </summary>
    [Fact]
    public void A_visually_hidden_table_is_hidden_by_a_wrapper_rather_than_by_itself()
    {
        var offenders = RazorFiles
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, number) => (File: Path.GetFileName(file), Number: number + 1, Line: line)))
            .Where(entry => Regex.IsMatch(entry.Line, @"<table[^>]*class=""[^""]*\bsr-only\b"))
            .Select(entry => $"{entry.File}:{entry.Number}")
            .ToArray();

        Assert.True(offenders.Length == 0, "sr-only applied directly to a table: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The stopgap for the list pages that still hold their table directly: the card
    /// scrolls rather than clipping. Without this rule the fix reaches only the pages
    /// that have adopted the component.
    /// </summary>
    [Fact]
    public void A_card_that_holds_a_table_directly_scrolls_instead_of_clipping()
    {
        var css = Css;

        Assert.Contains(".card:has(> .data-table)", css, StringComparison.Ordinal);
        Assert.Matches(@"\.card:has\(> \.data-table\)[^{]*\{[^}]*overflow-x:\s*auto", css);
    }

    [Fact]
    public void The_scroll_container_shows_that_there_is_more_to_the_side()
    {
        var css = Css;

        // The shade is painted with background-attachment: local, so it appears only
        // while the table actually overflows — no script, no resize observer.
        Assert.Matches(@"\.table-scroll\s*\{[^}]*overflow-x:\s*auto", css);
        Assert.Contains("no-repeat local", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_row_and_the_first_column_stay_put_while_the_table_scrolls()
    {
        var css = Css;

        Assert.Matches(@"\.table-scroll \.data-table thead th\s*\{[^}]*position:\s*sticky", css);
        Assert.Matches(@"\.table-scroll \.data-table th\.sticky-col[^{]*\{[^}]*position:\s*sticky", css);
    }

    /// <summary>
    /// iOS Safari zooms the layout in whenever a focused field is below 16 px and
    /// never zooms back out, so every tap on every form used to leave the reader
    /// pinching.
    /// </summary>
    [Fact]
    public void Form_fields_reach_sixteen_pixels_on_a_phone()
    {
        Assert.Matches(@"@media \(max-width: 720px\)[\s\S]*?\.input, \.search input \{ font-size: 16px; \}", Css);
    }

    /// <summary>
    /// A pixel root font-size silently ignores the browser's own "default font size"
    /// setting — the one thing a reader with low vision most often changes.
    /// </summary>
    [Fact]
    public void The_root_font_size_is_relative_to_the_readers_own_setting()
    {
        var css = Css;

        Assert.Matches(@"html \{ font-size: [\d.]+%; \}", css);
        Assert.DoesNotMatch(@"html, body \{[^}]*font-size:\s*\d+px", css);
    }

    /// <summary>
    /// Classes for an <c>EditForm</c> the app does not contain, and a default error
    /// boundary App.razor overrides — including the stylesheet's only untranslated
    /// English string.
    /// </summary>
    [Fact]
    public void The_stylesheet_carries_no_rules_that_can_never_match()
    {
        var css = Css;

        Assert.DoesNotContain(".validation-message", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".valid.modified", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".blazor-error-boundary", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// In a forced-colours theme the browser discards every author colour and every
    /// background image. Three things in this app carry data in exactly those two
    /// channels, so they have to opt out or change channel.
    /// </summary>
    [Fact]
    public void Forced_colours_keeps_the_places_where_colour_is_the_datum()
    {
        var css = Css;

        Assert.Contains("@media (forced-colors: active)", css, StringComparison.Ordinal);
        var block = css[css.IndexOf("@media (forced-colors: active)", StringComparison.Ordinal)..];
        Assert.Contains("forced-color-adjust: none", block, StringComparison.Ordinal);
        Assert.Contains(".job-color-0", block, StringComparison.Ordinal);
        // The hatching of a closed stretch is a background image and is stripped
        // regardless, so it changes to a border style, which survives.
        Assert.Contains(".gantt-closed.closed-break { border-style: dotted; }", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The drawer used to be hidden with a transform alone, which slides it off the
    /// screen and leaves its seven links in the tab order.
    /// </summary>
    [Fact]
    public void The_closed_mobile_drawer_leaves_the_tab_order()
    {
        Assert.Matches(@"@media \(max-width: 720px\)[\s\S]*?\.sidebar \{[^}]*visibility: hidden", Css);
    }
}
