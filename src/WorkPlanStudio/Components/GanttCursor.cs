using WorkPlanStudio.Services;

namespace WorkPlanStudio.Components;

/// <summary>
/// Which mark on the Gantt chart has the keyboard, and which one is being read out.
/// </summary>
/// <remarks>
/// <para>
/// This used to live in <c>Pages/Schedule.razor</c> as nine fields and methods
/// tangled into a 1,500-line component, which meant the chart's keyboard model —
/// the most intricate thing on the page and the one most likely to break
/// silently — could only be tested by rendering the whole schedule and pressing
/// keys at it. It is ordinary logic over a list of lists, so it is ordinary
/// logic over a list of lists now.
/// </para>
/// <para>
/// The model: the chart is <b>one</b> tab stop. A roving focus moves inside it
/// with the arrow keys, left and right along one work centre's day, up and down
/// between work centres keeping the position, so a reader can follow one moment
/// across the plant. Empty lanes are skipped, because a lane with nothing on it
/// cannot hold focus.
/// </para>
/// </remarks>
public sealed class GanttCursor
{
    private List<List<CellKey>> _lanes = [];

    /// <summary>The cell that owns the chart's single tab stop.</summary>
    public CellKey? Focus { get; private set; }

    /// <summary>The cell whose explanation is in the live region, if any.</summary>
    public CellKey? Selected { get; private set; }

    /// <summary>True when the chart has at least one mark to put focus on.</summary>
    public bool HasCells => Focus is not null;

    /// <summary>
    /// Rebuilds the lanes from a fresh result, dropping any focus and selection.
    /// </summary>
    /// <remarks>
    /// The two kinds of mark are rendered in separate loops — closed stretches
    /// paint underneath the bars — but the keyboard walks them as one sequence in
    /// time, which is why each lane is sorted by start rather than concatenated.
    /// </remarks>
    public void Rebuild(IReadOnlyList<GanttRow>? rows)
    {
        _lanes = [];
        Selected = null;
        Focus = null;

        if (rows is null)
            return;

        for (var lane = 0; lane < rows.Count; lane++)
        {
            var row = rows[lane];
            var cells = new List<(long Start, CellKey Key)>(row.Bars.Count + row.Closed.Count);
            for (var i = 0; i < row.Closed.Count; i++)
                cells.Add((row.Closed[i].StartSeconds, new CellKey(lane, false, i)));
            for (var i = 0; i < row.Bars.Count; i++)
                cells.Add((row.Bars[i].StartSeconds, new CellKey(lane, true, i)));
            _lanes.Add([.. cells.OrderBy(c => c.Start).Select(c => c.Key)]);
        }

        // One tab stop for the whole chart: the first cell that exists owns it.
        Focus = _lanes.FirstOrDefault(lane => lane.Count > 0)?.FirstOrDefault();
    }

    /// <summary>
    /// One lane's marks, in time order. The text alternative to the chart renders
    /// from this, so the table and the keyboard walk the same sequence rather than
    /// two that happen to agree today.
    /// </summary>
    public IReadOnlyList<CellKey> Lane(int index) =>
        index >= 0 && index < _lanes.Count ? _lanes[index] : [];

    /// <summary>True when <paramref name="key"/> is the chart's tab stop.</summary>
    public bool IsFocused(CellKey key) => Focus == key;

    /// <summary>True when <paramref name="key"/> is the one being read out.</summary>
    public bool IsSelected(CellKey key) => Selected == key;

    /// <summary>
    /// Enter, Space or a click: reads the cell out, or stops reading it when it was
    /// already the selected one. Returns true when something is now selected.
    /// </summary>
    public bool Toggle(CellKey key)
    {
        Focus = key;
        Selected = Selected == key ? null : key;
        return Selected is not null;
    }

    /// <summary>Escape: stop reading, keep the focus where it is.</summary>
    public void ClearSelection() => Selected = null;

    /// <summary>
    /// Moves the focus for an arrow, Home or End key pressed on <paramref name="from"/>.
    /// Returns true when the focus moved and the caller has to put the browser's
    /// focus on the new cell; false for every key this model does not handle.
    /// </summary>
    public bool Move(string key, CellKey from)
    {
        if (_lanes.Count == 0)
            return false;

        var lane = from.Lane;
        if (lane < 0 || lane >= _lanes.Count)
            return false;

        var position = _lanes[lane].IndexOf(from);
        if (position < 0)
            return false;

        switch (key)
        {
            case "ArrowRight": position++; break;
            case "ArrowLeft": position--; break;
            case "Home": position = 0; break;
            case "End": position = _lanes[lane].Count - 1; break;
            case "ArrowDown": lane = NextLane(lane, +1); break;
            case "ArrowUp": lane = NextLane(lane, -1); break;
            default: return false;
        }

        position = Math.Clamp(position, 0, _lanes[lane].Count - 1);
        Focus = _lanes[lane][position];
        return true;
    }

    /// <summary>Skips lanes with nothing on them; an empty lane cannot hold focus.</summary>
    private int NextLane(int from, int step)
    {
        for (var lane = from + step; lane >= 0 && lane < _lanes.Count; lane += step)
        {
            if (_lanes[lane].Count > 0)
                return lane;
        }
        return from;
    }
}

/// <summary>
/// One addressable mark on the chart: its lane, whether it is a bar or a closed
/// stretch, and its index within that kind.
/// </summary>
/// <remarks>
/// Index-within-kind rather than a flat position, because the two kinds are
/// rendered in separate loops while the keyboard walks them as one.
/// </remarks>
public readonly record struct CellKey(int Lane, bool IsBar, int Index)
{
    /// <summary>The element id the chart gives this mark, so focus can be put on it.</summary>
    public string ElementId => $"gantt-{(IsBar ? "bar" : "gap")}-{Lane}-{Index}";
}
