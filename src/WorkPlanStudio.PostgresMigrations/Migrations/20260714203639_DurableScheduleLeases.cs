using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPlanStudio.PostgresMigrations.Migrations;

/// <inheritdoc />
public partial class DurableScheduleLeases : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            table: "ScheduleRuns",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "CancellationRequestedUtc",
            table: "ScheduleRuns",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LeaseExpiresUtc",
            table: "ScheduleRuns",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LeaseOwner",
            table: "ScheduleRuns",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ScheduleRuns_Status_LeaseExpiresUtc",
            table: "ScheduleRuns",
            columns: new[] { "Status", "LeaseExpiresUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ScheduleRuns_Status_LeaseExpiresUtc",
            table: "ScheduleRuns");

        migrationBuilder.DropColumn(
            name: "AttemptCount",
            table: "ScheduleRuns");

        migrationBuilder.DropColumn(
            name: "CancellationRequestedUtc",
            table: "ScheduleRuns");

        migrationBuilder.DropColumn(
            name: "LeaseExpiresUtc",
            table: "ScheduleRuns");

        migrationBuilder.DropColumn(
            name: "LeaseOwner",
            table: "ScheduleRuns");
    }
}
