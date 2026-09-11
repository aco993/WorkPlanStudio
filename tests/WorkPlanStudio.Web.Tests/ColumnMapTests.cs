using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The auto-guess, which is the difference between a feature a planner uses and
/// one they abandon on the mapping screen. The header names here are the ones
/// real exports carry, in both languages and with the punctuation people put in
/// them.
/// </summary>
public sealed class ColumnMapTests
{
    private static ColumnMap Guess(ImportEntityKind kind, params string[] header) =>
        ColumnMap.Guess(ImportSchemas.For(kind), header);

    [Fact]
    public void English_headers_are_matched()
    {
        var map = Guess(ImportEntityKind.WorkCenters, "Code", "Name", "Cost center", "Hourly rate", "Parallel capacity", "Shift pattern", "Active");

        Assert.Empty(map.MissingRequired());
        Assert.Equal(0, map["Code"]);
        Assert.Equal(2, map["CostCenterCode"]);
        Assert.Equal(3, map["HourlyRate"]);
        Assert.Equal(6, map["IsActive"]);
    }

    [Fact]
    public void German_headers_are_matched()
    {
        var map = Guess(ImportEntityKind.WorkCenters, "Kennung", "Bezeichnung", "Kostenstelle", "Stundensatz", "Kapazität", "Schichtmodell", "Aktiv");

        Assert.Empty(map.MissingRequired());
        Assert.Equal(2, map["CostCenterCode"]);
        Assert.Equal(3, map["HourlyRate"]);
        Assert.Equal(4, map["ParallelCapacity"]);
    }

    /// <summary>
    /// An umlaut typed without its dots is the same word. Folding both spellings
    /// to the same name is what stops the mapping screen from being a puzzle.
    /// </summary>
    [Theory]
    [InlineData("Priorität")]
    [InlineData("Prioritaet")]
    [InlineData("PRIORITÄT")]
    [InlineData("Prioritat")]
    public void An_umlaut_is_matched_however_it_was_typed(string header)
    {
        var map = Guess(ImportEntityKind.ProductionOrders, "Auftrag", "Arbeitsplan", "Menge", "Freigabe", "Termin", header);

        Assert.Equal(5, map["Priority"]);
    }

    [Fact]
    public void Punctuation_and_units_in_a_header_do_not_stop_a_match()
    {
        var map = Guess(ImportEntityKind.WorkPlans,
            "Plan-Nr.", "Teile-Nummer", "Teilebezeichnung", "Rev.", "Status", "Losgröße",
            "AG-Nr.", "Arbeitsgang", "Arbeitsplatz-Code", "Rüstzeit [min]", "Stückzeit [min]", "Bemerkung");

        Assert.Empty(map.MissingRequired());
        Assert.Equal(6, map["OperationNumber"]);
        Assert.Equal(8, map["WorkCenterCode"]);
        Assert.Equal(9, map["SetupMinutes"]);
        Assert.Equal(10, map["RunMinutes"]);
    }

    [Fact]
    public void A_column_feeds_at_most_one_field()
    {
        var map = Guess(ImportEntityKind.CostCenters, "Code", "Name");

        Assert.Equal(0, map["Code"]);
        Assert.Equal(1, map["Name"]);
        Assert.Equal(ColumnMap.Unmapped, map["Description"]);
        Assert.Equal(ColumnMap.Unmapped, map["IsActive"]);
    }

    [Fact]
    public void Choosing_a_column_releases_it_from_whatever_had_it()
    {
        var map = Guess(ImportEntityKind.CostCenters, "Code", "Name");

        map.Set("Description", 1);

        Assert.Equal(1, map["Description"]);
        Assert.Equal(ColumnMap.Unmapped, map["Name"]);
    }

    [Fact]
    public void A_required_field_with_no_column_is_reported()
    {
        var map = Guess(ImportEntityKind.CostCenters, "Code", "Notiz");

        var missing = map.MissingRequired();

        Assert.Equal(["Name"], missing.Select(entry => entry.Key));
        Assert.Equal(1, map["Description"]);
    }

    [Fact]
    public void A_header_that_matches_nothing_leaves_every_field_unmapped()
    {
        var map = Guess(ImportEntityKind.CostCenters, "", "%%%%", "12345");

        Assert.Equal(2, map.MissingRequired().Count);
    }

    /// <summary>
    /// The reason a remembered mapping stores header names rather than positions:
    /// a monthly export that grows a column at the front would otherwise shift
    /// every field by one, silently, from the second import onwards.
    /// </summary>
    [Fact]
    public void A_remembered_mapping_survives_a_column_being_inserted()
    {
        var first = Guess(ImportEntityKind.CostCenters, "Code", "Name", "Note");
        first.Set("Description", 2);
        var remembered = first.ToRemembered();

        var second = ColumnMap.Restore(
            ImportSchemas.For(ImportEntityKind.CostCenters),
            ["Exported at", "Code", "Name", "Note"],
            remembered);

        Assert.Equal(1, second["Code"]);
        Assert.Equal(2, second["Name"]);
        Assert.Equal(3, second["Description"]);
    }

    [Fact]
    public void A_remembered_column_that_is_no_longer_in_the_file_falls_back_to_the_guess()
    {
        var remembered = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Code"] = "kennung",
            ["Name"] = "einespaltediees nichtmehrgibt"
        };

        var map = ColumnMap.Restore(ImportSchemas.For(ImportEntityKind.CostCenters), ["Code", "Name"], remembered);

        Assert.Equal(0, map["Code"]);
        Assert.Equal(1, map["Name"]);
    }

    [Fact]
    public void A_blank_header_cell_is_named_by_its_position_rather_than_matched()
    {
        var map = Guess(ImportEntityKind.CostCenters, "Code", "", "Name");

        Assert.Equal(0, map["Code"]);
        Assert.Equal(2, map["Name"]);
        Assert.Equal(ColumnMap.Unmapped, map["Description"]);
    }

    /// <summary>Every sheet the page offers ships an example that maps cleanly.</summary>
    [Theory]
    [InlineData(ImportEntityKind.CostCenters)]
    [InlineData(ImportEntityKind.WorkCenters)]
    [InlineData(ImportEntityKind.WorkPlans)]
    [InlineData(ImportEntityKind.ProductionOrders)]
    public async Task The_shipped_sample_file_maps_without_a_single_correction(ImportEntityKind kind)
    {
        var schema = ImportSchemas.For(kind);
        var path = Path.Join(RepoFiles.AppWwwroot, schema.SampleFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), "missing sample file: " + path);

        var header = (await CsvParserTests.ReadAsync(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), ';'))[0];
        var map = ColumnMap.Guess(schema, header.Fields);

        Assert.Empty(map.MissingRequired());
        Assert.All(schema.Fields, field => Assert.NotEqual(ColumnMap.Unmapped, map[field.Key]));
    }
}
