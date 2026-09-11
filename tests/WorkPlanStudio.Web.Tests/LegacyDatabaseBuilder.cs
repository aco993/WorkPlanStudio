using Microsoft.Data.Sqlite;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Builds a schema-5 database file — the shape the app shipped before cost
/// centres became master data.
/// <para>
/// Written out by hand rather than generated from the model on purpose. A
/// migration test that builds its "old" database from the <em>current</em> model
/// proves nothing: it would keep passing while the upgrade quietly stopped
/// matching anything a real visitor has in their browser.
/// </para>
/// </summary>
internal static class LegacyDatabaseBuilder
{
    public const string Ddl = """
        CREATE TABLE "CostCentersPlaceholder" ("Id" INTEGER NOT NULL PRIMARY KEY);
        DROP TABLE "CostCentersPlaceholder";

        CREATE TABLE "WorkCenters" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkCenters" PRIMARY KEY AUTOINCREMENT,
            "Code" TEXT COLLATE NOCASE NOT NULL,
            "Name" TEXT NOT NULL,
            "CostCenter" TEXT NULL,
            "HourlyRate" decimal(10,2) NOT NULL,
            "ParallelCapacity" INTEGER NOT NULL,
            "ShiftPatternKey" TEXT NOT NULL,
            "IsActive" INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX "IX_WorkCenters_Code" ON "WorkCenters" ("Code");

        CREATE TABLE "WorkCenterAbsences" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkCenterAbsences" PRIMARY KEY AUTOINCREMENT,
            "WorkCenterId" INTEGER NOT NULL,
            "Start" TEXT NOT NULL,
            "End" TEXT NOT NULL,
            "Kind" INTEGER NOT NULL,
            "Label" TEXT NULL
        );

        CREATE TABLE "PlantSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PlantSettings" PRIMARY KEY AUTOINCREMENT,
            "State" TEXT NOT NULL,
            "IncludePartialHolidays" INTEGER NOT NULL,
            "AllowExtendedDay" INTEGER NOT NULL,
            "AllowExtendedNight" INTEGER NOT NULL,
            "SundayWorkAllowed" INTEGER NOT NULL,
            "HolidayWorkAllowed" INTEGER NOT NULL,
            "SundayBoundaryShiftHours" INTEGER NOT NULL,
            "MinimumRestHours" INTEGER NOT NULL,
            "ModifiedUtc" TEXT NOT NULL
        );

        CREATE TABLE "WorkPlans" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkPlans" PRIMARY KEY AUTOINCREMENT,
            "PlanNumber" TEXT COLLATE NOCASE NOT NULL,
            "PartNumber" TEXT NOT NULL,
            "PartName" TEXT NOT NULL,
            "Revision" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "LotSize" INTEGER NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "ModifiedUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "IX_WorkPlans_PlanNumber" ON "WorkPlans" ("PlanNumber");

        CREATE TABLE "Operations" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Operations" PRIMARY KEY AUTOINCREMENT,
            "WorkPlanId" INTEGER NOT NULL,
            "OperationNumber" INTEGER NOT NULL,
            "Description" TEXT NOT NULL,
            "WorkCenterId" INTEGER NOT NULL,
            "SetupTimeMinutes" decimal(10,2) NOT NULL,
            "TimePerPieceMinutes" decimal(10,2) NOT NULL,
            "Remarks" TEXT NULL
        );

        CREATE TABLE "ProductionOrders" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ProductionOrders" PRIMARY KEY AUTOINCREMENT,
            "OrderNumber" TEXT NOT NULL,
            "WorkPlanId" INTEGER NOT NULL,
            "Quantity" INTEGER NOT NULL,
            "ReleaseUtc" TEXT NOT NULL,
            "DueUtc" TEXT NOT NULL,
            "Priority" INTEGER NOT NULL,
            "Status" INTEGER NOT NULL,
            "RoutingRevision" TEXT NOT NULL,
            "RoutingSnapshotJson" TEXT NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "ModifiedUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "IX_ProductionOrders_OrderNumber" ON "ProductionOrders" ("OrderNumber");
        """;

    /// <summary>Writes a schema-5 file with the given extra statements applied, and returns its Base64 payload.</summary>
    public static string BuildPayload(string path, string rows)
    {
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = Ddl + "\n" + rows;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var payload = Convert.ToBase64String(File.ReadAllBytes(path));
        File.Delete(path);
        return payload;
    }
}
