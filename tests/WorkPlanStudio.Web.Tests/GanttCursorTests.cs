using WorkPlanStudio.Components;
using WorkPlanStudio.Services;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The chart's keyboard model, tested without a chart.
/// </summary>
/// <remarks>
/// This behaviour was nine fields and methods inside a 1,500-line component, so
/// the only way to ask it a question was to render the whole schedule page and
/// press keys at it. Three of the cases below — the empty lane, the lane that is
/// shorter than the one above it, and a key the model does not handle — are ones
/// nobody would have written as a rendered test, which is the argument for
/// pulling the logic out rather than a side effect of it.
/// </remarks>
public class GanttCursorTests
{
    private static GanttBar Bar(long start, long end, int step = 1) =>
        new(JobId: 1, JobReference: "PO-1", ColorIndex: 0, StepNumber: step,
            StartSeconds: start, EndSeconds: end, IsLate: false);

    private static GanttClosedSegment Closed(long start, long end) =>
        new(start, end, SegmentKind.OffShift, "off shift");

    private static GanttRow Row(string name, IReadOnlyList<GanttBar> bars, IReadOnlyList<GanttClosedSegment>? closed = null) =>
        new(name, bars, closed ?? []);

    /// <summary>Two work centres: three marks on the first, two on the second.</summary>
    private static GanttCursor Chart()
    {
        var cursor = new GanttCursor();
        cursor.Rebuild([
            Row("CNC-200", [Bar(100, 200), Bar(300, 400)], [Closed(200, 300)]),
            Row("SAW-10", [Bar(0, 50), Bar(500, 600)])
        ]);
        return cursor;
    }

    [Fact]
    public void A_fresh_chart_puts_the_tab_stop_on_the_first_mark_in_time()
    {
        var cursor = Chart();

        // Not the first bar: the closed stretch sorts between the two bars, and
        // the lane is walked in time rather than by the order the loops paint in.
        Assert.Equal(new CellKey(0, true, 0), cursor.Focus);
        Assert.Null(cursor.Selected);
    }

    [Fact]
    public void A_lane_is_walked_in_time_not_in_render_order()
    {
        var cursor = Chart();

        Assert.True(cursor.Move("ArrowRight", new CellKey(0, true, 0)));
        Assert.Equal(new CellKey(0, false, 0), cursor.Focus);   // the closed stretch at 200
        Assert.True(cursor.Move("ArrowRight", cursor.Focus!.Value));
        Assert.Equal(new CellKey(0, true, 1), cursor.Focus);    // the bar at 300
    }

    [Fact]
    public void The_ends_of_a_lane_hold_rather_than_wrap()
    {
        var cursor = Chart();

        Assert.True(cursor.Move("ArrowLeft", new CellKey(0, true, 0)));
        Assert.Equal(new CellKey(0, true, 0), cursor.Focus);

        cursor.Move("End", cursor.Focus!.Value);
        var last = cursor.Focus;
        cursor.Move("ArrowRight", cursor.Focus!.Value);
        Assert.Equal(last, cursor.Focus);
    }

    [Fact]
    public void Home_and_End_jump_within_the_lane()
    {
        var cursor = Chart();

        Assert.True(cursor.Move("End", new CellKey(0, true, 0)));
        Assert.Equal(new CellKey(0, true, 1), cursor.Focus);
        Assert.True(cursor.Move("Home", cursor.Focus!.Value));
        Assert.Equal(new CellKey(0, true, 0), cursor.Focus);
    }

    [Fact]
    public void Moving_down_keeps_the_position_so_one_moment_can_be_followed_across_the_plant()
    {
        var cursor = Chart();
        cursor.Move("End", new CellKey(0, true, 0));            // position 2 on lane 0

        Assert.True(cursor.Move("ArrowDown", cursor.Focus!.Value));

        // Lane 1 has two marks, so position 2 clamps to the last one rather than
        // throwing or silently staying put.
        Assert.Equal(1, cursor.Focus!.Value.Lane);
        Assert.Equal(new CellKey(1, true, 1), cursor.Focus);
    }

    [Fact]
    public void An_empty_lane_is_skipped_because_it_cannot_hold_focus()
    {
        var cursor = new GanttCursor();
        cursor.Rebuild([
            Row("CNC-200", [Bar(0, 100)]),
            Row("IDLE-01", []),
            Row("SAW-10", [Bar(0, 100)])
        ]);

        Assert.True(cursor.Move("ArrowDown", new CellKey(0, true, 0)));
        Assert.Equal(2, cursor.Focus!.Value.Lane);
    }

    [Fact]
    public void A_chart_whose_every_lane_is_empty_has_no_tab_stop()
    {
        var cursor = new GanttCursor();
        cursor.Rebuild([Row("IDLE-01", []), Row("IDLE-02", [])]);

        Assert.Null(cursor.Focus);
        Assert.False(cursor.HasCells);
    }

    [Fact]
    public void A_key_the_model_does_not_handle_moves_nothing_and_says_so()
    {
        var cursor = Chart();
        var before = cursor.Focus;

        Assert.False(cursor.Move("PageDown", before!.Value));
        Assert.Equal(before, cursor.Focus);
    }

    [Fact]
    public void Activating_the_selected_cell_again_stops_reading_it()
    {
        var cursor = Chart();
        var cell = new CellKey(0, true, 0);

        Assert.True(cursor.Toggle(cell));
        Assert.True(cursor.IsSelected(cell));

        Assert.False(cursor.Toggle(cell));
        Assert.Null(cursor.Selected);
    }

    [Fact]
    public void Activating_a_cell_also_takes_the_tab_stop()
    {
        var cursor = Chart();
        var elsewhere = new CellKey(1, true, 1);

        cursor.Toggle(elsewhere);

        Assert.True(cursor.IsFocused(elsewhere));
    }

    [Fact]
    public void Escape_stops_reading_but_leaves_the_focus_alone()
    {
        var cursor = Chart();
        var cell = new CellKey(1, true, 0);
        cursor.Toggle(cell);

        cursor.ClearSelection();

        Assert.Null(cursor.Selected);
        Assert.True(cursor.IsFocused(cell));
    }

    [Fact]
    public void A_rebuild_drops_the_selection_so_a_new_run_reads_nothing_from_the_old_one()
    {
        var cursor = Chart();
        cursor.Toggle(new CellKey(0, true, 0));

        cursor.Rebuild([Row("CNC-200", [Bar(0, 100)])]);

        Assert.Null(cursor.Selected);
        Assert.Equal(new CellKey(0, true, 0), cursor.Focus);
    }

    [Fact]
    public void Rebuilding_from_nothing_clears_everything()
    {
        var cursor = Chart();
        cursor.Toggle(new CellKey(0, true, 0));

        cursor.Rebuild(null);

        Assert.Null(cursor.Focus);
        Assert.Null(cursor.Selected);
    }

    [Fact]
    public void A_key_pressed_on_a_cell_that_is_no_longer_there_is_ignored()
    {
        var cursor = Chart();
        cursor.Rebuild([Row("CNC-200", [Bar(0, 100)])]);

        // A keystroke can arrive for the chart that was on screen a moment ago.
        Assert.False(cursor.Move("ArrowRight", new CellKey(5, true, 9)));
    }

    [Fact]
    public void The_element_id_names_the_kind_so_focus_can_find_it_again()
    {
        Assert.Equal("gantt-bar-0-2", new CellKey(0, true, 2).ElementId);
        Assert.Equal("gantt-gap-3-1", new CellKey(3, false, 1).ElementId);
    }
}
