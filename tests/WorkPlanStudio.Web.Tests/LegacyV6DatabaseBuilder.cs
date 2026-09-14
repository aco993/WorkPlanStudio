using Microsoft.Data.Sqlite;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Builds a schema-6 database file — the shape the app shipped after cost
/// centres became master data and before the plant settings could name a § 3
/// reference period, a § 5 (2) sector or a § 11 (1) rota.
/// <para>
/// Written out by hand for the same reason
/// <see cref="LegacyDatabaseBuilder"/> is: a migration test that builds its
/// "old" database from the <em>current</em> model proves nothing. This DDL is a
/// transcription of what <c>EnsureCreated</c> produced from the schema-6 model,
/// and it must not be regenerated.
/// </para>
/// </summary>
internal static class LegacyV6DatabaseBuilder
{
    public const string Ddl = """
        CREATE TABLE "CostCenters" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CostCenters" PRIMARY KEY AUTOINCREMENT,
            "Code" TEXT COLLATE NOCASE NOT NULL,
            "Name" TEXT NOT NULL,
            "Description" TEXT NULL,
            "IsActive" INTEGER NOT NULL,
            CONSTRAINT "CK_CostCenter_Code" CHECK (length(trim(Code)) BETWEEN 1 AND 20)
        );
        CREATE UNIQUE INDEX "IX_CostCenters_Code" ON "CostCenters" ("Code");

        CREATE TABLE "WorkCenters" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkCenters" PRIMARY KEY AUTOINCREMENT,
            "Code" TEXT COLLATE NOCASE NOT NULL,
            "Name" TEXT NOT NULL,
            "CostCenterId" INTEGER NULL,
            "HourlyRate" TEXT NOT NULL,
            "ParallelCapacity" INTEGER NOT NULL,
            "ShiftPatternKey" TEXT NOT NULL,
            "IsActive" INTEGER NOT NULL,
            CONSTRAINT "FK_WorkCenters_CostCenters_CostCenterId" FOREIGN KEY ("CostCenterId") REFERENCES "CostCenters" ("Id")
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
            "ModifiedUtc" TEXT NOT NULL,
            CONSTRAINT "CK_PlantSettings_Rest" CHECK (MinimumRestHours BETWEEN 10 AND 11)
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
            "SetupTimeMinutes" TEXT NOT NULL,
            "TimePerPieceMinutes" TEXT NOT NULL,
            "Remarks" TEXT NULL
        );

        CREATE TABLE "ProductionOrders" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ProductionOrders" PRIMARY KEY AUTOINCREMENT,
            "OrderNumber" TEXT COLLATE NOCASE NOT NULL,
            "WorkPlanId" INTEGER NOT NULL,
            "Quantity" INTEGER NOT NULL,
            "ReleaseLocal" TEXT NOT NULL,
            "DueLocal" TEXT NOT NULL,
            "Priority" INTEGER NOT NULL,
            "Status" INTEGER NOT NULL,
            "RoutingRevision" TEXT NOT NULL,
            "RoutingSnapshotJson" TEXT NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "ModifiedUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "IX_ProductionOrders_OrderNumber" ON "ProductionOrders" ("OrderNumber");

        CREATE TABLE "OrderRoutingCenters" (
            "ProductionOrderId" INTEGER NOT NULL,
            "WorkCenterId" INTEGER NOT NULL,
            CONSTRAINT "PK_OrderRoutingCenters" PRIMARY KEY ("ProductionOrderId", "WorkCenterId")
        );
        CREATE INDEX "IX_OrderRoutingCenters_WorkCenterId" ON "OrderRoutingCenters" ("WorkCenterId");
        """;

    /// <summary>Writes a schema-6 file with the given extra statements applied, and returns its Base64 payload.</summary>
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
