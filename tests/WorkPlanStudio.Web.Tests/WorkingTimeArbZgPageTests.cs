using System.Globalization;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Components;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// What the Working-time page says about the Arbeitszeitgesetz, in the words a
/// reader actually gets: the § 3 sentence 2 average against its eight-hour
/// reference, the § 5 (2) sector gate, and the § 11 (1) free-Sunday count.
/// <para>
/// These render with the shipped English resource file rather than the
/// key-echoing localizer, because every assertion here is about a number or a
/// state word, and a key carries neither.
/// </para>
/// </summary>
public sealed class WorkingTimeArbZgPageTests : AppBunitContext, IDisposable
{
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentUICulture;

    public WorkingTimeArbZgPageTests() => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");

    void IDisposable.Dispose() => CultureInfo.CurrentUICulture = _previousCulture;

    private BrowserDatabase Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("arbzg.db", new FakeStorage());

        Services.AddSingleton<IStringLocalizer<SharedResource>>(new ResourceFileLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkPlanStudio.Services.Auth.WorkspaceRole.Planner);
        Services.AddSingleton(new WorkCenterService(database));
        Services.AddSingleton(new PlantSettingsService(database));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkingTimePage>>(NullLogger<WorkPlanStudio.Pages.WorkingTimePage>.Instance);
        return database;
    }

    private async Task<IRenderedComponent<WorkPlanStudio.Pages.WorkingTimePage>> PageAsync(TempDatabaseFiles files)
    {
        Assert.True((await Arrange(files).EnsureReadyAsync()).IsReady);
        var cut = Render<WorkPlanStudio.Pages.WorkingTimePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wt-pattern")));
        return cut;
    }

    /// <summary>The one row of the averaging table, as plain text.</summary>
    private static string AveragingRow(IRenderedComponent<WorkPlanStudio.Pages.WorkingTimePage> cut, string crew) =>
        cut.FindAll("table.wt-averaging tbody tr")
            .Select(row => row.TextContent)
            .Single(text => text.Contains(crew, StringComparison.Ordinal) && text.Contains("ArbZG", StringComparison.Ordinal));

    // ----- IR-4: the averaging reaches the screen -----

    [Fact]
    public async Task A_plant_inside_the_average_says_so_with_the_number_and_the_section()
    {
        using var files = new TempDatabaseFiles();
        var cut = await PageAsync(files);

        await cut.ActAsync("#wt-pattern", pattern => pattern.Change(ShiftPatterns.OneShift.Key));
        cut.WaitForAssertion(() => Assert.Contains("Day shift", cut.Markup, StringComparison.Ordinal));

        // 40 h a week over six Werktage is 6.67 h, well inside the eight hours
        // § 3 sentence 2 makes the ten-hour day conditional on.
        var row = AveragingRow(cut, "Day shift");
        Assert.Contains("§ 3 ArbZG", row, StringComparison.Ordinal);
        Assert.Contains("of at most 8", row, StringComparison.Ordinal);
        Assert.Contains("kept", row, StringComparison.Ordinal);
        Assert.Contains("not in this period", row, StringComparison.Ordinal);
        Assert.DoesNotContain("exceeded", row, StringComparison.Ordinal);

        // Measured over a real calendar rather than projected from the week, and
        // the Werktage the holidays took out are gone from the divisor.
        Assert.Contains("measured across the period", row, StringComparison.Ordinal);
        Assert.Contains("138 Werktage", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plant_over_the_average_gets_the_date_and_the_days_it_owes()
    {
        using var files = new TempDatabaseFiles();
        var cut = await PageAsync(files);

        await cut.ActAsync("#wt-pattern", pattern => pattern.Change(ShiftPatterns.OneShift.Key));
        cut.WaitForAssertion(() => Assert.Contains("Day shift", cut.Markup, StringComparison.Ordinal));
        await cut.ActAsync("#wt-sunday", sunday => sunday.Change(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wt-rotation")));
        await cut.ActAsync("#wt-days", days => days.Change("WithSunday"));

        // Seven 8-hour days is 56 h a week. Sunday hours count towards the § 3
        // average but Sunday is not a Werktag, so the divisor stays at six:
        // 1 296 h over 138 Werktage is 9.39 h, and 24 whole days have to come out
        // to bring it back to eight.
        cut.WaitForAssertion(() => Assert.Contains("exceeded", AveragingRow(cut, "Day shift"), StringComparison.Ordinal));
        var row = AveragingRow(cut, "Day shift");
        Assert.Contains("9.39", row, StringComparison.Ordinal);
        Assert.Contains("of at most 8", row, StringComparison.Ordinal);
        Assert.Contains("21 Jun 2026", row, StringComparison.Ordinal);
        Assert.Contains("24 days", row, StringComparison.Ordinal);
        Assert.DoesNotContain("kept", row, StringComparison.Ordinal);

        // The state is a word, not only a colour, and it also reaches the polite
        // live region so a screen reader hears the result of its own change.
        var live = cut.FindAll("[role='status'][aria-live='polite']").Select(e => e.TextContent).ToList();
        Assert.Contains(live, text => text.Contains("9.39", StringComparison.Ordinal) && text.Contains("exceeded", StringComparison.Ordinal));
    }

    // ----- IR-3: the § 5 (2) sector gate -----

    [Fact]
    public async Task The_ten_hour_rest_is_not_on_offer_until_a_sector_is_declared()
    {
        using var files = new TempDatabaseFiles();
        var cut = await PageAsync(files);

        Assert.Equal(["11"], cut.FindAll("#wt-rest option").Select(o => o.GetAttribute("value")));
        Assert.Contains("no entitlement to shorten the rest", cut.Find("#wt-rest-note").TextContent, StringComparison.Ordinal);

        await cut.ActAsync("#wt-sector", sector => sector.Change(nameof(RestExceptionSector.HealthCare)));

        cut.WaitForAssertion(() => Assert.Equal(["11", "10"], cut.FindAll("#wt-rest option").Select(o => o.GetAttribute("value"))));

        // The control that appeared says what it costs: § 5 (2) buys the hour back
        // with a twelve-hour rest elsewhere.
        Assert.Contains("at least 12", cut.Find("#wt-rest-note").TextContent, StringComparison.Ordinal);
        Assert.Contains(
            cut.FindAll("[role='status'][aria-live='polite']").Select(e => e.TextContent),
            text => text.Contains("shortened rest of 10 hours is now on offer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Taking_the_sector_away_puts_the_rest_back_to_eleven_hours()
    {
        using var files = new TempDatabaseFiles();
        var cut = await PageAsync(files);

        await cut.ActAsync("#wt-sector", sector => sector.Change(nameof(RestExceptionSector.Hospitality)));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#wt-rest option").Count));
        await cut.ActAsync("#wt-rest", rest => rest.Change("10"));
        cut.WaitForAssertion(() => Assert.Equal("10", cut.Find("#wt-rest").GetAttribute("value")));

        await cut.ActAsync("#wt-sector", sector => sector.Change(nameof(RestExceptionSector.None)));

        // The entitlement goes and the value goes with it, rather than being left
        // behind for the validator to refuse on the next save.
        cut.WaitForAssertion(() => Assert.Equal("11", cut.Find("#wt-rest").GetAttribute("value")));
        Assert.Single(cut.FindAll("#wt-rest option"));
    }

    [Fact]
    public async Task A_ten_hour_rest_cannot_be_persisted_without_a_sector()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("sector.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var service = new PlantSettingsService(database);

        var refused = await service.SaveAsync(
            new PlantSettings { MinimumRestHours = 10, RestExceptionSector = RestExceptionSector.None },
            cancellationToken);

        Assert.Equal(ApplicationResultStatus.ValidationFailed, refused.Status);
        var issue = Assert.Single(refused.ValidationIssues!, i => i.MessageKey == "Wt_Val_RestNeedsSector");
        Assert.Equal(nameof(PlantSettings.MinimumRestHours), issue.Field);

        var accepted = await service.SaveAsync(
            new PlantSettings { MinimumRestHours = 10, RestExceptionSector = RestExceptionSector.Transport },
            cancellationToken);
        Assert.True(accepted.IsSuccess);
    }

    [Fact]
    public async Task The_table_refuses_the_shortened_rest_even_when_the_validator_is_bypassed()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("check.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "UPDATE PlantSettings SET MinimumRestHours = 10, RestExceptionSector = 0 WHERE Id = 1;";

        // Hiding the control is not a rule. An import, the API or a hand-edited
        // payload never passes the form, so the table says it as well.
        var failure = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => command.ExecuteNonQueryAsync(cancellationToken));
        Assert.Contains("CK_PlantSettings_RestSector", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sector_check_constraint_covers_exactly_the_sectors_the_library_defines()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("sectors.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        await using var db = await database.CreateContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();

        // The CHECK has to spell its bound as a literal. A sector added to the
        // library and not to the constraint would be refused by the database and
        // by nothing else, which is the kind of failure nobody reproduces.
        int highest = Enum.GetValues<RestExceptionSector>().Max(sector => (int)sector);
        foreach (var sector in Enum.GetValues<RestExceptionSector>())
        {
            await using var accept = connection.CreateCommand();
            accept.CommandText = $"UPDATE PlantSettings SET MinimumRestHours = 11, RestExceptionSector = {(int)sector} WHERE Id = 1;";
            Assert.Equal(1, await accept.ExecuteNonQueryAsync(cancellationToken));
        }

        await using var refuse = connection.CreateCommand();
        refuse.CommandText = $"UPDATE PlantSettings SET RestExceptionSector = {highest + 1} WHERE Id = 1;";
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => refuse.ExecuteNonQueryAsync(cancellationToken));
    }

    // ----- § 11 (1): the free Sundays -----

    [Fact]
    public async Task A_declared_rota_turns_a_permanent_breach_into_a_real_count()
    {
        using var files = new TempDatabaseFiles();
        var cut = await PageAsync(files);

        await cut.ActAsync("#wt-pattern", pattern => pattern.Change(ShiftPatterns.OneShift.Key));
        cut.WaitForAssertion(() => Assert.Contains("Day shift", cut.Markup, StringComparison.Ordinal));
        await cut.ActAsync("#wt-sunday", sunday => sunday.Change(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wt-rotation")));
        await cut.ActAsync("#wt-days", days => days.Change("WithSunday"));

        // One crew, no rota: it works all 52 Sundays of 2026 and none stays free.
        cut.WaitForAssertion(() => Assert.Contains("0 free Sundays in 2026", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Too few Sundays stay free", cut.Markup, StringComparison.Ordinal);

        await cut.ActAsync("#wt-rotation", rotation => rotation.Change("4"));

        // Four crews taking one Sunday in four leave 39 free, which is over the
        // fifteen § 11 (1) asks for — lawful, and no longer reported as a breach.
        cut.WaitForAssertion(() => Assert.Contains("39 free Sundays in 2026", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("rota keeps enough Sundays free", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Too few Sundays stay free", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rota_control_only_exists_where_sunday_work_is_permitted()
    {
        using var files = new TempDatabaseFiles();
        var cut = await PageAsync(files);

        Assert.Empty(cut.FindAll("#wt-rotation"));

        await cut.ActAsync("#wt-sunday", sunday => sunday.Change(true));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#wt-rotation")));
        Assert.Equal("wt-rotation-hint", cut.Find("#wt-rotation").GetAttribute("aria-describedby"));
        Assert.Contains(
            cut.FindAll("[role='status'][aria-live='polite']").Select(e => e.TextContent),
            text => text.Contains("crew rota now decides", StringComparison.Ordinal));
    }

    // ----- IR-6: a week the rules closed says why -----

    [Fact]
    public void A_closed_week_names_the_rule_that_closed_it()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new ResourceFileLocalizer<SharedResource>());

        // Staffed on Sunday only, and § 9 forbids Sunday work: every block goes,
        // and the week is closed rather than unconstrained.
        var pattern = new ShiftPattern("sunday-only", [new ShiftDefinition("day", new TimeOnly(8, 0), new TimeOnly(16, 0), WorkDays.Sunday)]);
        var timeline = WorkingTimelineBuilder.Build(
            pattern, WorkingTimeRules.Statutory, [], new DateTime(2026, 1, 5), new DateTime(2026, 1, 12));
        Assert.IsType<WeekCapacity.Closed>(timeline.Capacity);

        var cut = Render<WeekStrip>(parameters => parameters.Add(c => c.Timeline, timeline));

        var note = cut.Find(".week-closed").TextContent;
        Assert.Contains("no working time at all", note, StringComparison.Ordinal);
        Assert.Contains("§ 9 ArbZG", note, StringComparison.Ordinal);
        Assert.Contains("Sunday rest", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_staffed_week_says_nothing_about_being_closed()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new ResourceFileLocalizer<SharedResource>());
        var timeline = WorkingTimelineBuilder.Build(
            ShiftPatterns.TwoShift, WorkingTimeRules.Statutory, [], new DateTime(2026, 1, 5), new DateTime(2026, 1, 12));

        var cut = Render<WeekStrip>(parameters => parameters.Add(c => c.Timeline, timeline));

        Assert.Empty(cut.FindAll(".week-closed"));
    }

    // ----- the settings the page now writes -----

    [Fact]
    public async Task The_averaging_window_and_the_rota_reach_the_rules_and_the_database()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("settings.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var saved = await new PlantSettingsService(database).SaveAsync(
            new PlantSettings
            {
                AveragingWindow = AveragingWindow.SixCalendarMonths,
                SundayWorkAllowed = true,
                SundayRotationWeeks = 4
            },
            cancellationToken);
        Assert.True(saved.IsSuccess);

        var reloaded = await new PlantSettingsService(files.CreateDatabase("settings-2.db", storage)).GetAsync(cancellationToken);
        Assert.Equal(AveragingWindow.SixCalendarMonths, reloaded.ToRules().AveragingWindow);
        Assert.Equal(4, reloaded.ToRules().SundayRotationWeeks);
    }

    [Fact]
    public async Task A_rota_of_zero_weeks_is_refused_rather_than_dividing_by_it()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("rota.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var result = await new PlantSettingsService(database).SaveAsync(
            new PlantSettings { SundayRotationWeeks = 0 }, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationResultStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationIssues!, i => i.Field == nameof(PlantSettings.SundayRotationWeeks));
    }
}
