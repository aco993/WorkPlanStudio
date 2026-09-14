namespace WorkPlanStudio.Export.Pdf;

/// <summary>
/// A vertical cursor over a document: it knows the current page and how far
/// down it the next thing goes, and it starts a new page when what comes next
/// no longer fits.
/// <para>
/// This is the whole of the layout model, and deliberately so. Blocks ask for
/// the space they need one row at a time and draw at the cursor, which is
/// enough for a report of stacked tables and a chart, and far less machinery
/// than a general flow layout would be.
/// </para>
/// </summary>
public sealed class PdfLayout
{
    private readonly PdfDocumentWriter _writer;
    private readonly Func<PdfPage, double>? _onPageStarted;

    /// <summary>
    /// Starts a layout on a fresh page.
    /// </summary>
    /// <param name="writer">The document pages are added to.</param>
    /// <param name="onPageStarted">
    /// Draws whatever every page carries - a running header, a rule - and
    /// returns the y coordinate content may start at. Omitted, content starts
    /// at the top margin.
    /// </param>
    public PdfLayout(PdfDocumentWriter writer, Func<PdfPage, double>? onPageStarted = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _onPageStarted = onPageStarted;
        Page = StartPage();
    }

    /// <summary>The page being drawn on.</summary>
    public PdfPage Page { get; private set; }

    /// <summary>The cursor: the y coordinate, from the top of the page, the next block draws at.</summary>
    public double Y { get; set; }

    /// <summary>Left edge of the content area.</summary>
    public double Left => Page.ContentLeft;

    /// <summary>Right edge of the content area.</summary>
    public double Right => Page.ContentRight;

    /// <summary>Bottom edge of the content area.</summary>
    public double Bottom => Page.ContentBottom;

    /// <summary>Width of the content area.</summary>
    public double Width => Page.ContentWidth;

    /// <summary>Space left on the current page below the cursor.</summary>
    public double Remaining => Bottom - Y;

    /// <summary>Moves the cursor down.</summary>
    public void Advance(double points) => Y += points;

    /// <summary>Starts a new page and puts the cursor at the top of its content area.</summary>
    public PdfPage NewPage()
    {
        Page = StartPage();
        return Page;
    }

    /// <summary>
    /// Guarantees <paramref name="height"/> points below the cursor, starting a
    /// new page if there are not. Returns true when it did, so a block can
    /// repeat its own header.
    /// </summary>
    public bool EnsureSpace(double height)
    {
        if (Remaining >= height)
            return false;

        NewPage();
        return true;
    }

    private PdfPage StartPage()
    {
        var page = _writer.AddPage();
        Y = _onPageStarted?.Invoke(page) ?? page.ContentTop;
        return page;
    }
}

/// <summary>Something that can draw itself into a <see cref="PdfLayout"/>, paginating as it goes.</summary>
public interface IPdfBlock
{
    /// <summary>Draws at the layout's cursor and leaves the cursor below what it drew.</summary>
    void Render(PdfLayout layout);
}
