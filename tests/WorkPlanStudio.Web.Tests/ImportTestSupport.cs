using System.Text;
using WorkPlanStudio.Data;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// One seeded browser database, the import service over it, and the master-data
/// services used to check what actually landed. The import is asserted through
/// the same read paths the pages use, never by reaching into the context.
/// </summary>
internal sealed record ImportShop(
    BrowserDatabase Database,
    FakeStorage Storage,
    CsvImportService Importer,
    CostCenterService CostCenters,
    WorkCenterService Centers,
    WorkPlanService Plans,
    ProductionOrderService Orders);

internal static class ImportTestSupport
{
    public static async Task<ImportShop> ArrangeAsync(
        TempDatabaseFiles files, string name, IPermissionGuard? guard = null)
    {
        var storage = new FakeStorage();
        var database = files.CreateDatabase(name, storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        return new ImportShop(
            database,
            storage,
            new CsvImportService(database, storage, guard),
            new CostCenterService(database),
            new WorkCenterService(database),
            new WorkPlanService(database),
            new ProductionOrderService(database));
    }

    /// <summary>Runs the dry run the way the page does: inspect, guess the mapping, plan.</summary>
    public static async Task<ImportPlan> PlanAsync(
        this ImportShop shop, string csv, ImportOptions options, CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes(csv);
        var header = await shop.Importer.InspectAsync(content, options.Separator, cancellationToken);
        Assert.True(header.IsSuccess, "the header could not be read");

        var map = ColumnMap.Guess(ImportSchemas.For(options.Kind), header.Value!.Columns);
        var planned = await shop.Importer.PlanAsync(content, options, map, null, cancellationToken);
        Assert.True(planned.IsSuccess, Describe(planned));

        return planned.Value!;
    }

    public static string Describe<T>(ApplicationResult<T> result) =>
        $"{result.Status}: " + string.Join(
            ", ", (result.ValidationIssues ?? []).Select(issue => issue.MessageKey));

    /// <summary>Every message key in the rejected list, for an assertion that reads like a sentence.</summary>
    public static string[] Reasons(this ImportPlan plan) =>
        [.. plan.Rejected.Select(issue => issue.MessageKey)];
}
