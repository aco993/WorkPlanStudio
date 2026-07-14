using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkPlanStudio.Persistence.Migrations.Sqlite;

/// <inheritdoc />
public partial class InitialProductionSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AspNetRoles",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUsers",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUsers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AuditEntries",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                ActorId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                EntityType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                EntityId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ChangesJson = table.Column<string>(type: "text", nullable: true),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEntries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ScheduleRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                ParametersJson = table.Column<string>(type: "text", nullable: false),
                ResultJson = table.Column<string>(type: "text", nullable: true),
                ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                Version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScheduleRuns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkCenters",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                CostCenter = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                HourlyRate = table.Column<double>(type: "REAL", nullable: false),
                ParallelCapacity = table.Column<int>(type: "INTEGER", nullable: false),
                TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                Version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkCenters", x => x.Id);
                table.CheckConstraint("CK_WorkCenter_Capacity", "\"ParallelCapacity\" BETWEEN 1 AND 64");
                table.CheckConstraint("CK_WorkCenter_Rate", "\"HourlyRate\" BETWEEN 0 AND 1000000");
            });

        migrationBuilder.CreateTable(
            name: "WorkPlans",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                PlanNumber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                PartNumber = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                PartName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                Revision = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                LotSize = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkPlans", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetRoleClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RoleId = table.Column<string>(type: "TEXT", nullable: false),
                ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserLogins",
            columns: table => new
            {
                LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                UserId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                table.ForeignKey(
                    name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserRoles",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                RoleId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserTokens",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                table.ForeignKey(
                    name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CalendarShifts",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WorkCenterId = table.Column<int>(type: "INTEGER", nullable: false),
                DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                StartMinute = table.Column<int>(type: "INTEGER", nullable: false),
                EndMinute = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CalendarShifts", x => x.Id);
                table.CheckConstraint("CK_CalendarShift_Range", "\"StartMinute\" >= 0 AND \"EndMinute\" <= 1440 AND \"EndMinute\" > \"StartMinute\"");
                table.ForeignKey(
                    name: "FK_CalendarShifts_WorkCenters_WorkCenterId",
                    column: x => x.WorkCenterId,
                    principalTable: "WorkCenters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MachineDowntimes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WorkCenterId = table.Column<int>(type: "INTEGER", nullable: false),
                StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MachineDowntimes", x => x.Id);
                table.CheckConstraint("CK_Downtime_Range", "\"EndUtc\" > \"StartUtc\"");
                table.ForeignKey(
                    name: "FK_MachineDowntimes_WorkCenters_WorkCenterId",
                    column: x => x.WorkCenterId,
                    principalTable: "WorkCenters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SetupTransitions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WorkCenterId = table.Column<int>(type: "INTEGER", nullable: false),
                FromFamily = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                ToFamily = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SetupTransitions", x => x.Id);
                table.CheckConstraint("CK_SetupTransition_Duration", "\"DurationMinutes\" BETWEEN 0 AND 10080");
                table.ForeignKey(
                    name: "FK_SetupTransitions_WorkCenters_WorkCenterId",
                    column: x => x.WorkCenterId,
                    principalTable: "WorkCenters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Operations",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WorkPlanId = table.Column<int>(type: "INTEGER", nullable: false),
                OperationNumber = table.Column<int>(type: "INTEGER", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                WorkCenterId = table.Column<int>(type: "INTEGER", nullable: false),
                SetupTimeMinutes = table.Column<double>(type: "REAL", nullable: false),
                TimePerPieceMinutes = table.Column<double>(type: "REAL", nullable: false),
                SetupFamily = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                Remarks = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Operations", x => x.Id);
                table.ForeignKey(
                    name: "FK_Operations_WorkCenters_WorkCenterId",
                    column: x => x.WorkCenterId,
                    principalTable: "WorkCenters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Operations_WorkPlans_WorkPlanId",
                    column: x => x.WorkPlanId,
                    principalTable: "WorkPlans",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ProductionOrders",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                OrderNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                WorkPlanId = table.Column<int>(type: "INTEGER", nullable: false),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                ReleaseUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                DueUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Priority = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                RoutingRevision = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                RoutingSnapshotJson = table.Column<string>(type: "text", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductionOrders", x => x.Id);
                table.CheckConstraint("CK_ProductionOrder_Dates", "\"DueUtc\" > \"ReleaseUtc\"");
                table.CheckConstraint("CK_ProductionOrder_Priority", "\"Priority\" BETWEEN 1 AND 10");
                table.CheckConstraint("CK_ProductionOrder_Quantity", "\"Quantity\" BETWEEN 1 AND 1000000");
                table.ForeignKey(
                    name: "FK_ProductionOrders_WorkPlans_WorkPlanId",
                    column: x => x.WorkPlanId,
                    principalTable: "WorkPlans",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AspNetRoleClaims_RoleId",
            table: "AspNetRoleClaims",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            table: "AspNetRoles",
            column: "NormalizedName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserClaims_UserId",
            table: "AspNetUserClaims",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserLogins_UserId",
            table: "AspNetUserLogins",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserRoles_RoleId",
            table: "AspNetUserRoles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "AspNetUsers",
            column: "NormalizedEmail");

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "AspNetUsers",
            column: "NormalizedUserName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntries_OwnerId_OccurredUtc",
            table: "AuditEntries",
            columns: new[] { "OwnerId", "OccurredUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CalendarShifts_WorkCenterId_DayOfWeek_StartMinute",
            table: "CalendarShifts",
            columns: new[] { "WorkCenterId", "DayOfWeek", "StartMinute" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MachineDowntimes_WorkCenterId_StartUtc_EndUtc",
            table: "MachineDowntimes",
            columns: new[] { "WorkCenterId", "StartUtc", "EndUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Operations_WorkCenterId",
            table: "Operations",
            column: "WorkCenterId");

        migrationBuilder.CreateIndex(
            name: "IX_Operations_WorkPlanId_OperationNumber",
            table: "Operations",
            columns: new[] { "WorkPlanId", "OperationNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProductionOrders_OwnerId_OrderNumber",
            table: "ProductionOrders",
            columns: new[] { "OwnerId", "OrderNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProductionOrders_WorkPlanId",
            table: "ProductionOrders",
            column: "WorkPlanId");

        migrationBuilder.CreateIndex(
            name: "IX_ScheduleRuns_OwnerId_CreatedUtc",
            table: "ScheduleRuns",
            columns: new[] { "OwnerId", "CreatedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_SetupTransitions_WorkCenterId_FromFamily_ToFamily",
            table: "SetupTransitions",
            columns: new[] { "WorkCenterId", "FromFamily", "ToFamily" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkCenters_OwnerId_Code",
            table: "WorkCenters",
            columns: new[] { "OwnerId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkPlans_OwnerId_PlanNumber",
            table: "WorkPlans",
            columns: new[] { "OwnerId", "PlanNumber" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AspNetRoleClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserLogins");

        migrationBuilder.DropTable(
            name: "AspNetUserRoles");

        migrationBuilder.DropTable(
            name: "AspNetUserTokens");

        migrationBuilder.DropTable(
            name: "AuditEntries");

        migrationBuilder.DropTable(
            name: "CalendarShifts");

        migrationBuilder.DropTable(
            name: "MachineDowntimes");

        migrationBuilder.DropTable(
            name: "Operations");

        migrationBuilder.DropTable(
            name: "ProductionOrders");

        migrationBuilder.DropTable(
            name: "ScheduleRuns");

        migrationBuilder.DropTable(
            name: "SetupTransitions");

        migrationBuilder.DropTable(
            name: "AspNetRoles");

        migrationBuilder.DropTable(
            name: "AspNetUsers");

        migrationBuilder.DropTable(
            name: "WorkPlans");

        migrationBuilder.DropTable(
            name: "WorkCenters");
    }
}
