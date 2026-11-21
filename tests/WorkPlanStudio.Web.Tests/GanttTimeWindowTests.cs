using WorkPlanStudio.Components;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The calendar windows the Gantt chart can show. A planner's "Monday" runs from
/// midnight, whatever time the run began, and a week starts on a Monday; these hold
/// the arithmetic to that.
/// </summary>
public sealed class GanttTimeWindowTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    // The seed plant's horizon: Monday 1 June 2026, 06:00.
    private static readonly DateTime Monday0600 = new(2026, 6, 1, 6, 0, 0);

    [Fact]
    public void A_span_is_offered_only_when_the_run_does_not_fit_in_one()
    {
        Assert.Equal([GanttSpan.All], GanttTimeWindow.Offered(20 * Hour, Monday0600));
        Assert.Equal([GanttSpan.All, GanttSpan.Day], GanttTimeWindow.Offered(40 * Hour, Monday0600));
        Assert.Equal([GanttSpan.All, GanttSpan.Week, GanttSpan.Day], GanttTimeWindow.Offered(175 * Hour, Monday0600));
        Assert.Equal([GanttSpan.All, GanttSpan.Month, GanttSpan.Week, GanttSpan.Day], GanttTimeWindow.Offered(40 * Day, Monday0600));
    }

    [Fact]
    public void A_month_needs_a_calendar()
    {
        Assert.DoesNotContain(GanttSpan.Month, GanttTimeWindow.Offered(40 * Day, horizon: null));
    }

    [Fact]
    public void Days_run_from_midnight_even_when_the_run_starts_at_six()
    {
        var days = GanttTimeWindow.Windows(GanttSpan.Day, 40 * Hour, Monday0600);

        Assert.Equal([new GanttWindow(-6 * Hour, 18 * Hour), new GanttWindow(18 * Hour, 42 * Hour)], days);
    }

    [Fact]
    public void Weeks_run_from_monday_midnight()
    {
        var wednesday = new DateTime(2026, 6, 3, 14, 30, 0);
        var weeks = GanttTimeWindow.Windows(GanttSpan.Week, 10 * Day, wednesday);

        var firstMonday = wednesday.AddSeconds(weeks[0].StartSeconds);
        Assert.Equal(new DateTime(2026, 6, 1), firstMonday);
        Assert.All(weeks, w => Assert.Equal(7 * Day, w.Length));
        Assert.Equal(2, weeks.Count);
    }

    [Fact]
    public void Months_follow_the_calendar_not_thirty_days()
    {
        var months = GanttTimeWindow.Windows(GanttSpan.Month, 60 * Day, Monday0600);

        Assert.Equal(new DateTime(2026, 6, 1), Monday0600.AddSeconds(months[0].StartSeconds));
        Assert.Equal(new DateTime(2026, 7, 1), Monday0600.AddSeconds(months[0].EndSeconds));
        Assert.Equal(30 * Day, months[0].Length);          // June
        Assert.Equal(31 * Day, months[1].Length);          // July
        Assert.Equal(2, months.Count);                     // the run ends on 31 July 06:00
    }

    [Fact]
    public void Without_a_horizon_windows_count_from_the_start_of_the_run()
    {
        Assert.Equal([new GanttWindow(0, Day), new GanttWindow(Day, 2 * Day)], GanttTimeWindow.Windows(GanttSpan.Day, 30 * Hour, horizon: null));
    }

    [Fact]
    public void The_whole_plan_is_one_window()
    {
        Assert.Equal([new GanttWindow(0, 175 * Hour)], GanttTimeWindow.Windows(GanttSpan.All, 175 * Hour, Monday0600));
    }

    [Fact]
    public void The_window_holding_a_moment_is_found_and_past_the_end_is_the_last()
    {
        var days = GanttTimeWindow.Windows(GanttSpan.Day, 40 * Hour, Monday0600);

        Assert.Equal(0, GanttTimeWindow.IndexOf(days, 0));
        Assert.Equal(1, GanttTimeWindow.IndexOf(days, 18 * Hour));
        Assert.Equal(1, GanttTimeWindow.IndexOf(days, 100 * Hour));
    }
}
