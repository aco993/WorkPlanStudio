using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Import;
using ImportPage = WorkPlanStudio.Pages.DataImport;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The page, through the two things a reviewer would check: that nothing is
/// written before the preview has been read, and that the preview is usable by
/// somebody who cannot see it.
/// </summary>
public sealed class DataImportPageTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private const string GoodFile =
        "Code;Name;Description;Active\n" +
        "CC-7100;Prototyping;First articles;yes\n" +
        "CC-7200;Finishing;;yes\n";

    private const string MixedFile =
        "Code;Name\n" +
        "CC-7100;Prototyping\n" +
        ";No code at all\n";

    private BrowserDatabase Arrange(WorkspaceRole role = WorkspaceRole.Planner)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new FakeStorage();
        var database = _files.CreateDatabase($"page-{Guid.NewGuid():N}.db", storage);

        Services.AddLogging();
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(database);
        Services.AddSingleton<IBrowserDatabaseStorage>(storage);
        Services.AddSingleton(provider => new CsvImportService(
            database, storage, provider.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<IImportMappingStore>(new FakeMappingStore());
        Services.AddSingleton(new CostCenterService(database));
        return database;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private static async Task UploadAsync(IRenderedComponent<ImportPage> cut, string csv, string name = "cost-centers.csv")
    {
        // One statement: finding the component and raising its event in two of
        // them re-renders in between and the handler that is invoked belongs to a
        // node that is no longer in the tree.
        await cut.InvokeAsync(() =>
            cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(csv, name)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#map-Code")));
    }

    private static async Task DryRunAsync(IRenderedComponent<ImportPage> cut)
    {
        await cut.InvokeAsync(() => cut.Find("#import-dry-run").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#import-commit, .import-counts")));
    }

    // ----- the drop zone --------------------------------------------------

    [Fact]
    public void The_file_control_has_a_real_label_and_a_description_that_exists()
    {
        Arrange();

        var cut = Render<ImportPage>();

        var input = cut.Find("input[type=file]");
        var label = cut.Find("label[for=import-file]");
        Assert.Equal("import-file", input.Id);
        Assert.False(string.IsNullOrWhiteSpace(label.TextContent));

        var describedBy = input.GetAttribute("aria-describedby");
        Assert.False(string.IsNullOrWhiteSpace(describedBy));
        Assert.NotEmpty(cut.FindAll($"#{describedBy}"));
    }

    /// <summary>
    /// The live region has to be in the DOM before its content changes: one
    /// inserted already populated is announced by no screen reader.
    /// </summary>
    [Fact]
    public async Task The_status_region_exists_empty_before_anything_is_imported()
    {
        Arrange();
        var cut = Render<ImportPage>();

        var region = cut.Find("#import-status");
        Assert.Equal("polite", region.GetAttribute("aria-live"));
        Assert.Equal("status", region.GetAttribute("role"));
        Assert.Equal("", region.TextContent.Trim());

        await UploadAsync(cut, GoodFile);
        await DryRunAsync(cut);

        // The same element, now carrying the result — and carrying only the
        // result. Progress<T> posts its reports, so the last "reading…" one used
        // to land after the answer and overwrite it, leaving a screen reader with
        // the progress as the permanent announcement.
        cut.WaitForAssertion(() => Assert.Contains(
            "Import_Progress_Done", cut.Find("#import-status").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain(
            "Import_Progress_Reading", cut.Find("#import-status").TextContent, StringComparison.Ordinal);
    }

    // ----- the mapping ----------------------------------------------------

    [Fact]
    public async Task Every_mapping_control_is_a_labelled_select_over_the_file_s_columns()
    {
        Arrange();
        var cut = Render<ImportPage>();

        await UploadAsync(cut, GoodFile);

        foreach (var entry in ImportSchemas.For(ImportEntityKind.CostCenters).Fields)
        {
            var select = cut.Find($"#map-{entry.Key}");
            Assert.Equal("SELECT", select.NodeName);
            Assert.NotEmpty(cut.FindAll($"label[for=map-{entry.Key}]"));
        }

        // "not imported" plus one option per column in the file.
        Assert.Equal(5, cut.FindAll("#map-Code option").Count);
    }

    [Fact]
    public async Task A_required_field_without_a_column_blocks_the_dry_run_and_says_which()
    {
        Arrange();
        var cut = Render<ImportPage>();
        await UploadAsync(cut, GoodFile);

        await cut.InvokeAsync(() => cut.Find("#map-Name").Change("-1"));

        cut.WaitForAssertion(() => Assert.True(cut.Find("#import-dry-run").HasAttribute("disabled")));
        var select = cut.Find("#map-Name");
        Assert.Equal("true", select.GetAttribute("aria-invalid"));
        Assert.Equal("map-Name-error", select.GetAttribute("aria-describedby"));
        Assert.NotEmpty(cut.FindAll("#map-Name-error"));
    }

    // ----- the dry run ----------------------------------------------------

    [Fact]
    public async Task The_dry_run_shows_a_preview_and_writes_nothing()
    {
        var database = Arrange();
        var cut = Render<ImportPage>();

        await UploadAsync(cut, GoodFile);
        await DryRunAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr[id^=import-row-]").Count));

        // Still nothing in the database: the commit button exists precisely
        // because the write has not happened yet.
        Assert.NotEmpty(cut.FindAll("#import-commit"));
        Assert.DoesNotContain(
            await new CostCenterService(database).GetAllAsync(Xunit.TestContext.Current.CancellationToken),
            costCenter => costCenter.Code == "CC-7100");
    }

    [Fact]
    public async Task The_preview_is_a_table_with_a_caption_and_column_headers()
    {
        Arrange();
        var cut = Render<ImportPage>();
        await UploadAsync(cut, GoodFile);
        await DryRunAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table.data-table caption")));

        var table = cut.FindAll("table.data-table")[0];
        Assert.NotNull(table.QuerySelector("caption"));
        Assert.All(table.QuerySelectorAll("thead th"), header => Assert.Equal("col", header.GetAttribute("scope")));
        Assert.All(table.QuerySelectorAll("tbody th"), header => Assert.Equal("row", header.GetAttribute("scope")));
    }

    /// <summary>
    /// A reason with no row to point at is a list of line numbers. The preview row
    /// links to its reason and the reason links back, so either end is a way in.
    /// </summary>
    [Fact]
    public async Task A_rejected_row_and_its_reason_link_to_each_other()
    {
        Arrange();
        var cut = Render<ImportPage>();

        await UploadAsync(cut, MixedFile);
        await DryRunAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#import-issue-3")));

        var link = cut.Find("#import-row-3 a");
        Assert.Equal("#import-issue-3", link.GetAttribute("href"));

        // Several links reading "why" one after another are useless in a list of
        // links, which is how a screen-reader user navigates a table like this, so
        // each one carries a name of its own. The test localizer echoes keys, so
        // the line number in it is asserted against the resource itself below.
        Assert.False(string.IsNullOrWhiteSpace(link.GetAttribute("aria-label")));

        var back = cut.Find("#import-issue-3 a");
        Assert.Equal("#import-row-3", back.GetAttribute("href"));
    }

    [Fact]
    public async Task The_rejected_rows_can_be_downloaded_as_a_file()
    {
        Arrange();
        var cut = Render<ImportPage>();

        await UploadAsync(cut, MixedFile);
        await DryRunAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#import-rejected-download")));

        var download = cut.Find("#import-rejected-download");
        Assert.StartsWith("data:text/csv;base64,", download.GetAttribute("href"), StringComparison.Ordinal);
        Assert.EndsWith(".csv", download.GetAttribute("download"), StringComparison.Ordinal);
    }

    // ----- committing -----------------------------------------------------

    [Fact]
    public async Task Pressing_import_writes_the_rows_and_announces_what_happened()
    {
        var database = Arrange();
        var cut = Render<ImportPage>();

        await UploadAsync(cut, GoodFile);
        await DryRunAsync(cut);
        await cut.InvokeAsync(() => cut.Find("#import-commit").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "Import_Result", cut.Find("#import-status").TextContent, StringComparison.Ordinal));

        var stored = await new CostCenterService(database).GetAllAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.Contains(stored, costCenter => costCenter.Code == "CC-7100");
        Assert.Contains(stored, costCenter => costCenter.Code == "CC-7200");

        // The plan is gone with the file, so the same import cannot be applied twice.
        Assert.Empty(cut.FindAll("#import-commit"));
    }

    // ----- the guest ------------------------------------------------------

    /// <summary>
    /// The button is hidden and the notice says why. The service refuses either
    /// way — this is the courtesy layer, and it is the half a reviewer sees.
    /// </summary>
    [Fact]
    public async Task A_guest_is_told_the_page_is_read_only_and_gets_no_import_button()
    {
        Arrange(WorkspaceRole.Guest);
        var cut = Render<ImportPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".readonly-notice")));

        await UploadAsync(cut, GoodFile);
        await DryRunAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".import-counts")));
        Assert.Empty(cut.FindAll("#import-commit"));
    }

    /// <summary>
    /// The accessible names that distinguish one otherwise identical link from
    /// another have to carry the line number, in both languages.
    /// </summary>
    [Theory]
    [InlineData("SharedResource.resx")]
    [InlineData("SharedResource.de.resx")]
    public void The_link_names_that_point_between_the_tables_carry_the_line_number(string file)
    {
        var resources = System.Xml.Linq.XDocument
            .Load(Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Resources", file))
            .Root!
            .Elements("data")
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")!.Value);

        Assert.Contains("{0}", resources["Import_Row_Why_Label"], StringComparison.Ordinal);
        Assert.Contains("{0}", resources["Import_Row_Back"], StringComparison.Ordinal);
    }

    private sealed class FakeMappingStore : IImportMappingStore
    {
        private readonly Dictionary<ImportEntityKind, IReadOnlyDictionary<string, string>> _saved = [];

        public ValueTask<IReadOnlyDictionary<string, string>> LoadAsync(
            ImportEntityKind kind, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_saved.GetValueOrDefault(
                kind, new Dictionary<string, string>(StringComparer.Ordinal)));

        public ValueTask SaveAsync(
            ImportEntityKind kind, IReadOnlyDictionary<string, string> mapping, CancellationToken cancellationToken = default)
        {
            _saved[kind] = mapping;
            return ValueTask.CompletedTask;
        }
    }
}
