namespace WorkPlanStudio.Services.Forms;

/// <summary>
/// Which slice of a list is on screen.
/// <para>
/// The lists used to draw every row they had. Measured on the published site with
/// 300 imported orders — well inside the 8 MB the import page invites — the
/// production-orders page rendered <b>269 rows as 4 717 DOM nodes with 531
/// buttons</b>, took about 3.9 s to arrive and about 0.78 s to redraw for a single
/// status filter. The engine was never the problem: the demo schedule runs in
/// 390 ms in the same browser.
/// </para>
/// <para>
/// Paging rather than virtualising, because every row here carries real controls:
/// a window that swaps rows in and out as the reader scrolls moves focus out from
/// under the keyboard and makes the row count a moving target for a screen reader.
/// A page is a fixed, announceable thing — and the page buttons are ordinary
/// buttons.
/// </para>
/// </summary>
public sealed class PageWindow
{
    /// <summary>Rows per page. Fifty fills a tall screen without filling the DOM.</summary>
    public const int DefaultSize = 50;

    public int Size { get; init; } = DefaultSize;

    public int Page { get; private set; } = 1;

    /// <summary>How many rows the current filter matches, across all pages.</summary>
    public int Total { get; private set; }

    public int PageCount => Total <= 0 ? 1 : (Total + Size - 1) / Size;

    /// <summary>1-based, for "showing 51–100 of 269". Zero when nothing matches.</summary>
    public int FirstShown => Total == 0 ? 0 : ((Page - 1) * Size) + 1;

    public int LastShown => Math.Min(Page * Size, Total);

    /// <summary>
    /// Takes the page's worth of rows out of what the filter matched.
    /// <para>
    /// A change in how many rows match goes back to page one: filtering to seven
    /// rows while standing on page four would otherwise show an empty table and
    /// nothing to explain it. Sorting keeps the page, because sorting does not
    /// change how many there are, and a reader who sorted from page four meant
    /// page four.
    /// </para>
    /// </summary>
    public IEnumerable<T> Slice<T>(IReadOnlyCollection<T> matched)
    {
        ArgumentNullException.ThrowIfNull(matched);

        if (matched.Count != Total)
        {
            Total = matched.Count;
            Page = 1;
        }

        return matched.Skip((Page - 1) * Size).Take(Size);
    }

    public void GoTo(int page) => Page = Math.Clamp(page, 1, PageCount);

    /// <summary>
    /// The page numbers worth drawing: always the first and the last, and a short
    /// run around where the reader is. Three hundred pages of buttons is the same
    /// mistake one row further out.
    /// </summary>
    public IReadOnlyList<int?> Numbers()
    {
        if (PageCount <= 7)
            return Enumerable.Range(1, PageCount).Select(n => (int?)n).ToList();

        var near = Enumerable.Range(Math.Max(2, Page - 1), 3).Where(n => n < PageCount).ToList();
        var numbers = new List<int?> { 1 };

        if (near[0] > 2)
            numbers.Add(null);          // a gap, drawn as an ellipsis

        numbers.AddRange(near.Select(n => (int?)n));

        if (near[^1] < PageCount - 1)
            numbers.Add(null);

        numbers.Add(PageCount);
        return numbers;
    }
}
