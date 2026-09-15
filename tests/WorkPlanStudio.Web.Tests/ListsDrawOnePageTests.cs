using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Forms;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The lists used to draw every row they had. Measured on the published site with
/// 300 imported orders — well inside the 8 MB the import page invites — the
/// production-orders page rendered 269 rows as 4 717 DOM nodes with 531 buttons,
/// took ~3.9 s to arrive and ~0.78 s to redraw for one status filter.
/// </summary>
public sealed class ListsDrawOnePageTests : AppBunitContext
{
    private static PageWindow Small() => new() { Size = 10 };

    private static IReadOnlyCollection<int> Rows(int count) => Enumerable.Range(1, count).ToList();

    // ----- the window itself -----

    [Fact]
    public void A_short_list_is_one_page_and_all_of_it()
    {
        var window = Small();

        var shown = window.Slice(Rows(7)).ToList();

        Assert.Equal(7, shown.Count);
        Assert.Equal(1, window.PageCount);
        Assert.Equal(1, window.FirstShown);
        Assert.Equal(7, window.LastShown);
    }

    [Fact]
    public void A_long_list_gives_out_one_page_at_a_time()
    {
        var window = Small();
        _ = window.Slice(Rows(269));

        window.GoTo(3);
        var third = window.Slice(Rows(269)).ToList();

        Assert.Equal(10, third.Count);
        Assert.Equal(21, third[0]);
        Assert.Equal("21", window.FirstShown.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(30, window.LastShown);
        Assert.Equal(27, window.PageCount);
    }

    [Fact]
    public void The_last_page_is_the_remainder_not_a_full_one()
    {
        var window = Small();
        _ = window.Slice(Rows(269));
        window.GoTo(27);

        var last = window.Slice(Rows(269)).ToList();

        Assert.Equal(9, last.Count);
        Assert.Equal(269, window.LastShown);
    }

    [Fact]
    public void Asking_for_a_page_that_is_not_there_lands_on_one_that_is()
    {
        var window = Small();
        _ = window.Slice(Rows(25));

        window.GoTo(99);
        Assert.Equal(3, window.Page);

        window.GoTo(-4);
        Assert.Equal(1, window.Page);
    }

    [Fact]
    public void Filtering_to_fewer_rows_goes_back_to_the_first_page()
    {
        // Otherwise the reader filters from page four and meets an empty table
        // with nothing on screen to explain where their rows went.
        var window = Small();
        _ = window.Slice(Rows(269));
        window.GoTo(4);

        var afterFilter = window.Slice(Rows(7)).ToList();

        Assert.Equal(1, window.Page);
        Assert.Equal(7, afterFilter.Count);
    }

    [Fact]
    public void Sorting_keeps_the_page_you_are_standing_on()
    {
        // Sorting does not change how many rows there are, and a reader who sorted
        // from page four meant page four.
        var window = Small();
        _ = window.Slice(Rows(269));
        window.GoTo(4);

        _ = window.Slice(Rows(269).Reverse().ToList());

        Assert.Equal(4, window.Page);
    }

    [Fact]
    public void Many_pages_are_offered_as_a_few_buttons_and_the_ends()
    {
        // Three hundred pages of buttons is the same mistake one row further out.
        var window = Small();
        _ = window.Slice(Rows(1000));     // 100 pages
        window.GoTo(50);

        var numbers = window.Numbers();

        Assert.Equal(1, numbers[0]);
        Assert.Equal(100, numbers[^1]);
        Assert.Contains(50, numbers);
        Assert.Contains(null, numbers);                       // the gaps are drawn as ellipses
        Assert.True(numbers.Count <= 9, $"offered {numbers.Count} buttons");
    }

    // ----- and the half that is easy to get wrong -----

    [Fact]
    public void Turning_a_page_tells_the_list_to_redraw()
    {
        // The window belongs to the page, so changing it inside the pager
        // re-renders the pager and nothing else. The first version of this did
        // exactly that: the range said "101-150 of 269" while the table underneath
        // went on showing rows 1-50. A pager that lies about which rows you are
        // looking at is worse than no pager at all.
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var window = Small();
        _ = window.Slice(Rows(269));
        var told = 0;

        var cut = Render<WorkPlanStudio.Components.Paginator>(p => p
            .Add(x => x.Window, window)
            .Add(x => x.OnChanged, EventCallback.Factory.Create(this, () => told++)));

        cut.FindAll(".pager-controls button").Single(b => b.TextContent.Trim() == "3").Click();

        Assert.Equal(3, window.Page);
        Assert.Equal(1, told);
    }

    [Fact]
    public void A_list_that_fits_on_one_page_shows_no_pager()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var window = Small();
        _ = window.Slice(Rows(7));

        var cut = Render<WorkPlanStudio.Components.Paginator>(p => p
            .Add(x => x.Window, window)
            .Add(x => x.OnChanged, EventCallback.Factory.Create(this, () => { })));

        Assert.Empty(cut.FindAll("nav.pager"));
    }

    [Fact]
    public void The_pager_says_where_you_are_out_loud()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var window = Small();
        _ = window.Slice(Rows(269));
        window.GoTo(2);

        var cut = Render<WorkPlanStudio.Components.Paginator>(p => p
            .Add(x => x.Window, window)
            .Add(x => x.OnChanged, EventCallback.Factory.Create(this, () => { })));

        Assert.Equal("status", cut.Find(".pager-range").GetAttribute("role"));
        Assert.NotNull(cut.Find("nav.pager").GetAttribute("aria-label"));
        Assert.Equal("2", cut.FindAll(".pager-controls button")
            .Single(b => b.GetAttribute("aria-current") == "page").TextContent.Trim());
    }
}
