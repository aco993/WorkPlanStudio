using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPlanStudio.Api.Data.Migrations;

/// <inheritdoc />
public partial class PlantSettingsArbZgSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AveragingWindow",
            table: "PlantSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "RestExceptionSector",
            table: "PlantSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        // 1, not 0: a rota of zero weeks is not a rota, and § 11 (1) divides
        // by it. A row written before the column existed declared no rota at
        // all, and one week is what "no rota" means.
        migrationBuilder.AddColumn<int>(
            name: "SundayRotationWeeks",
            table: "PlantSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AveragingWindow",
            table: "PlantSettings");

        migrationBuilder.DropColumn(
            name: "RestExceptionSector",
            table: "PlantSettings");

        migrationBuilder.DropColumn(
            name: "SundayRotationWeeks",
            table: "PlantSettings");
    }
}
