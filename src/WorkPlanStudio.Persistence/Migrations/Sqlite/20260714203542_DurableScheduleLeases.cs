using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPlanStudio.Persistence.Migrations.Sqlite;

/// <inheritdoc />
public partial class DurableScheduleLeases : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            table: "ScheduleRuns",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "CancellationRequestedUtc",
            table: "ScheduleRuns",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LeaseExpiresUtc",
            table: "ScheduleRuns",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LeaseOwner",
            table: "ScheduleRuns",
            type: "TEXT",
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
