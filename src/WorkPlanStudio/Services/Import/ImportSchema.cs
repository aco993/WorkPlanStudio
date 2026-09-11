using System.Text;

namespace WorkPlanStudio.Services.Import;

/// <summary>Which sheet is being imported. Each has its own columns, keys and rules.</summary>
public enum ImportEntityKind
{
    /// <summary>Cost centres: the master data everything else refers to by code.</summary>
    CostCenters,

    /// <summary>Work centres, naming their cost centre by code rather than by key.</summary>
    WorkCenters,

    /// <summary>Work plans and their operations — several rows per plan.</summary>
    WorkPlans,

    /// <summary>Production orders, naming their routing by plan number.</summary>
    ProductionOrders
}

/// <summary>One field a sheet can feed, and the header names it answers to.</summary>
/// <param name="Key">Stable identifier used in the mapping and in messages.</param>
/// <param name="LabelKey">Resource key for the label on the mapping screen.</param>
/// <param name="Required">A required field with no column blocks the import.</param>
/// <param name="Aliases">
/// Normalised header names, English and German. Matching is done on these rather
/// than on the property name: nobody exports a column called
/// <c>TimePerPieceMinutes</c>, they export <c>Stückzeit</c>.
/// </param>
public sealed record ImportField(string Key, string LabelKey, bool Required, IReadOnlyList<string> Aliases);

/// <summary>The columns of one sheet.</summary>
/// <param name="Kind">Which entity the sheet feeds.</param>
/// <param name="TitleKey">Resource key naming it on screen.</param>
/// <param name="SampleFile">The downloadable example, relative to <c>wwwroot</c>.</param>
/// <param name="Fields">Its fields, in the order a mapping screen lists them.</param>
public sealed record ImportSchema(
    ImportEntityKind Kind,
    string TitleKey,
    string SampleFile,
    IReadOnlyList<ImportField> Fields)
{
    /// <summary>The fields that must be mapped before anything can be imported.</summary>
    public IEnumerable<ImportField> Required => Fields.Where(entry => entry.Required);
}

/// <summary>The four sheets this application can read, and how header names are matched.</summary>
public static class ImportSchemas
{
    /// <summary>
    /// Folds a header cell to the form aliases are written in: lower case, letters
    /// and digits only, umlauts expanded.
    /// </summary>
    /// <remarks>
    /// The expansion is what makes <c>Priorität</c>, <c>Prioritaet</c> and
    /// <c>PRIORITÄT</c> one name. The non-expanded spelling (<c>prioritat</c>,
    /// which is what stripping a diacritic gives) is listed explicitly where it is
    /// plausible, because a header typed without the umlaut is common and folding
    /// both ways would also merge words that are genuinely different.
    /// </remarks>
    public static string Normalise(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return "";

        var builder = new StringBuilder(header.Length);
        foreach (var character in header.Trim().ToLowerInvariant())
        {
            switch (character)
            {
                case 'ä': builder.Append("ae"); break;
                case 'ö': builder.Append("oe"); break;
                case 'ü': builder.Append("ue"); break;
                case 'ß': builder.Append("ss"); break;
                default:
                    if (char.IsLetterOrDigit(character))
                        builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static readonly ImportSchema CostCentersSchema = new(
        ImportEntityKind.CostCenters,
        "Import_Kind_CostCenters",
        "samples/cost-centers.csv",
    [
        new("Code", "Import_Field_Code", true,
            ["code", "costcenter", "costcentre", "costcentercode", "kostenstelle", "kostenstellennummer", "kst", "nummer", "number"]),
        new("Name", "Import_Field_Name", true,
            ["name", "bezeichnung", "costcentername", "kostenstellenname", "benennung"]),
        new("Description", "Import_Field_Description", false,
            ["description", "beschreibung", "note", "notiz", "bemerkung", "remarks"]),
        new("IsActive", "Import_Field_Active", false,
            ["active", "isactive", "aktiv", "status"])
    ]);

    private static readonly ImportSchema WorkCentersSchema = new(
        ImportEntityKind.WorkCenters,
        "Import_Kind_WorkCenters",
        "samples/work-centers.csv",
    [
        new("Code", "Import_Field_Code", true,
            ["code", "workcenter", "workcentre", "workcentercode", "arbeitsplatz", "arbeitsplatzcode", "arbeitsplatznummer", "kennung", "machine", "maschine"]),
        new("Name", "Import_Field_Name", true,
            ["name", "bezeichnung", "workcentername", "arbeitsplatzname", "benennung"]),
        new("CostCenterCode", "Import_Field_CostCenter", false,
            ["costcenter", "costcentre", "costcentercode", "kostenstelle", "kst"]),
        new("HourlyRate", "Import_Field_HourlyRate", false,
            ["hourlyrate", "rate", "stundensatz", "kostensatz", "satz", "maschinenstundensatz"]),
        new("ParallelCapacity", "Import_Field_Capacity", false,
            ["capacity", "parallelcapacity", "kapazitaet", "kapazitat", "parallelkapazitaet", "parallelkapazitat", "anzahl"]),
        new("ShiftPatternKey", "Import_Field_ShiftPattern", false,
            ["shiftpattern", "shiftpatternkey", "shift", "schichtmodell", "schicht"]),
        new("IsActive", "Import_Field_Active", false,
            ["active", "isactive", "aktiv", "status"])
    ]);

    private static readonly ImportSchema WorkPlansSchema = new(
        ImportEntityKind.WorkPlans,
        "Import_Kind_WorkPlans",
        "samples/work-plans.csv",
    [
        new("PlanNumber", "Import_Field_PlanNumber", true,
            ["plannumber", "planno", "plannr", "plan", "workplan", "routing", "arbeitsplan", "arbeitsplannummer", "plannummer"]),
        new("PartNumber", "Import_Field_PartNumber", false,
            ["partnumber", "partno", "partnr", "part", "teilenummer", "artikelnummer", "sachnummer", "teil"]),
        new("PartName", "Import_Field_PartName", true,
            ["partname", "partdescription", "teilebezeichnung", "benennung", "bezeichnung"]),
        new("Revision", "Import_Field_Revision", false,
            ["revision", "rev", "aenderungsstand", "anderungsstand", "stand", "index"]),
        new("Status", "Import_Field_Status", false,
            ["status", "planstatus", "zustand"]),
        new("LotSize", "Import_Field_LotSize", false,
            ["lotsize", "lot", "losgroesse", "losgrosse", "los"]),
        new("OperationNumber", "Import_Field_OperationNumber", true,
            ["operationnumber", "operationno", "opnumber", "opno", "op", "agnr", "agno", "ag", "arbeitsgangnummer", "arbeitsgangnr", "vorgangsnummer", "operation", "arbeitsgang"]),
        new("OperationDescription", "Import_Field_OperationDescription", true,
            ["operationdescription", "operationtext", "description", "vorgang", "vorgangstext", "arbeitsgangbezeichnung", "arbeitsgangtext", "taetigkeit", "tatigkeit", "beschreibung"]),
        new("WorkCenterCode", "Import_Field_WorkCenter", true,
            ["workcenter", "workcentre", "workcentercode", "arbeitsplatz", "arbeitsplatzcode", "maschine", "machine"]),
        new("SetupMinutes", "Import_Field_SetupMinutes", false,
            ["setuptime", "setupminutes", "setup", "ruestzeit", "rustzeit", "ruesten"]),
        new("RunMinutes", "Import_Field_RunMinutes", false,
            ["runtime", "runminutes", "timeperpiece", "minutesperpiece", "perpiece", "stueckzeit", "stuckzeit", "einzelzeit", "zeitprostueck"]),
        new("Remarks", "Import_Field_Remarks", false,
            ["remarks", "note", "bemerkung", "notiz", "hinweis"])
    ]);

    private static readonly ImportSchema ProductionOrdersSchema = new(
        ImportEntityKind.ProductionOrders,
        "Import_Kind_Orders",
        "samples/production-orders.csv",
    [
        new("OrderNumber", "Import_Field_OrderNumber", true,
            ["ordernumber", "orderno", "ordernr", "order", "auftrag", "auftragsnummer", "fertigungsauftrag", "fanummer"]),
        new("PlanNumber", "Import_Field_PlanNumber", true,
            ["plannumber", "planno", "plannr", "workplan", "plan", "routing", "arbeitsplan", "arbeitsplannummer"]),
        new("Quantity", "Import_Field_Quantity", true,
            ["quantity", "qty", "menge", "stueckzahl", "stuckzahl", "anzahl", "losgroesse"]),
        new("ReleaseLocal", "Import_Field_Release", true,
            ["release", "releasedate", "start", "startdate", "freigabe", "freigabedatum", "startdatum", "beginn"]),
        new("DueLocal", "Import_Field_Due", true,
            ["due", "duedate", "deadline", "termin", "liefertermin", "endtermin", "faelligkeit", "falligkeit", "enddatum"]),
        new("Priority", "Import_Field_Priority", false,
            ["priority", "prio", "prioritaet", "prioritat"])
    ]);

    /// <summary>Every sheet, in the order the page offers them — dependencies first.</summary>
    public static IReadOnlyList<ImportSchema> All { get; } =
        [CostCentersSchema, WorkCentersSchema, WorkPlansSchema, ProductionOrdersSchema];

    public static ImportSchema For(ImportEntityKind kind) =>
        All.First(schema => schema.Kind == kind);
}
