using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Models;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Data;

/// <summary>
/// EF Core context for the whole app. On WebAssembly this talks to a SQLite
/// database that lives in the browser's virtual file system.
/// </summary>
public class AppDbContext : DbContext
{
    /// <summary>
    /// The column type for a <c>decimal</c>. Deliberately <c>TEXT</c> and not
    /// <c>decimal(10,2)</c>: SQLite derives a column's affinity from substrings
    /// of its declared type, and <c>decimal(10,2)</c> contains none of
    /// INT/CHAR/CLOB/TEXT/BLOB/REAL/FLOA/DOUB, so its affinity is NUMERIC — it
    /// converts EF's text representation into an IEEE-754 double on the way in.
    /// Money and minutes then round-trip through a binary float while the column
    /// claims to be a decimal with two places, which SQLite does not enforce
    /// either. TEXT affinity stores exactly what EF sends and reads back the
    /// same decimal.
    /// <para>
    /// The trap this leaves behind is why the CHECK constraints below cast: on a
    /// TEXT column <c>SetupTimeMinutes &lt;= 1000000</c> would be a string
    /// comparison, and <c>'9' &gt; '1000'</c> — silently inverting every numeric
    /// range check on the table. The scale the old type claimed is now enforced
    /// where it can actually be enforced, in the validators.
    /// </para>
    /// </summary>
    internal const string DecimalColumnType = "TEXT";

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<WorkPlan> WorkPlans => Set<WorkPlan>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<OrderRoutingCenter> OrderRoutingCenters => Set<OrderRoutingCenter>();
    public DbSet<WorkCenterAbsence> WorkCenterAbsences => Set<WorkCenterAbsence>();
    public DbSet<PlantSettings> PlantSettings => Set<PlantSettings>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CostCenter>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(250);
            e.HasIndex(x => x.Code).IsUnique();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_CostCenter_Code", "length(trim(Code)) BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_CostCenter_Name", "length(trim(Name)) BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_CostCenter_Description", "Description IS NULL OR length(Description) <= 250");
            });
        });

        model.Entity<WorkCenter>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.HourlyRate).HasColumnType(DecimalColumnType);
            e.Property(x => x.ShiftPatternKey).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_WorkCenter_HourlyRate", "CAST(HourlyRate AS REAL) BETWEEN 0 AND 1000000");
                table.HasCheckConstraint("CK_WorkCenter_ParallelCapacity", "ParallelCapacity >= 1 AND ParallelCapacity <= 64");
                table.HasCheckConstraint("CK_WorkCenter_Code", "length(trim(Code)) BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_WorkCenter_Name", "length(trim(Name)) BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_WorkCenter_ShiftPattern", "length(trim(ShiftPatternKey)) BETWEEN 1 AND 40");
            });

            // Restrict, not cascade: retiring a cost centre must not take the
            // machines that book against it with it.
            e.HasOne(x => x.CostCenter)
             .WithMany(c => c.WorkCenters)
             .HasForeignKey(x => x.CostCenterId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.Absences)
             .WithOne(a => a.WorkCenter!)
             .HasForeignKey(a => a.WorkCenterId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WorkCenterAbsence>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(80);
            e.HasIndex(x => new { x.WorkCenterId, x.Start });
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Absence_Range", "\"End\" > \"Start\"");
                table.HasCheckConstraint("CK_Absence_Kind", "Kind >= 0 AND Kind <= 3");
            });
        });

        model.Entity<PlantSettings>(e =>
        {
            e.Property(x => x.State).HasMaxLength(2).IsRequired();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_PlantSettings_Singleton", "Id = 1");
                table.HasCheckConstraint("CK_PlantSettings_SundayShift", "SundayBoundaryShiftHours BETWEEN 0 AND 6");
                table.HasCheckConstraint("CK_PlantSettings_Rest", "MinimumRestHours BETWEEN 10 AND 11");

                // § 5 (2) reserves the shortened rest for the sectors it names.
                // The form only offers the choice once a sector is declared and
                // the validator refuses it, but neither of those is in the way of
                // an import or of a row written by the API, so the table says it
                // too. The sector bound is pinned to the library's enum by a test.
                table.HasCheckConstraint(
                    "CK_PlantSettings_RestSector",
                    $"MinimumRestHours = 11 OR RestExceptionSector <> {(int)RestExceptionSector.None}");
                table.HasCheckConstraint("CK_PlantSettings_Sector", "RestExceptionSector BETWEEN 0 AND 5");
                table.HasCheckConstraint("CK_PlantSettings_Averaging", "AveragingWindow BETWEEN 0 AND 1");
                table.HasCheckConstraint("CK_PlantSettings_SundayRotation", "SundayRotationWeeks BETWEEN 1 AND 52");
            });
        });

        model.Entity<WorkPlan>(e =>
        {
            e.Property(x => x.PlanNumber).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.PartNumber).HasMaxLength(40).UseCollation("NOCASE");
            e.Property(x => x.PartName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Revision).HasMaxLength(10).UseCollation("NOCASE");
            e.HasIndex(x => x.PlanNumber).IsUnique();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_WorkPlan_LotSize", "LotSize >= 1 AND LotSize <= 1000000");
                table.HasCheckConstraint("CK_WorkPlan_Status", "Status >= 0 AND Status <= 2");
                table.HasCheckConstraint("CK_WorkPlan_PlanNumber", "length(trim(PlanNumber)) BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_WorkPlan_PartNumber", "length(PartNumber) <= 40");
                table.HasCheckConstraint("CK_WorkPlan_PartName", "length(trim(PartName)) BETWEEN 1 AND 120");
                table.HasCheckConstraint("CK_WorkPlan_Revision", "Revision IS NULL OR length(Revision) <= 10");
            });

            e.HasMany(x => x.Operations)
             .WithOne(o => o.WorkPlan!)
             .HasForeignKey(o => o.WorkPlanId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Operation>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(120).IsRequired();
            e.Property(x => x.SetupTimeMinutes).HasColumnType(DecimalColumnType);
            e.Property(x => x.TimePerPieceMinutes).HasColumnType(DecimalColumnType);
            e.Property(x => x.Remarks).HasMaxLength(250);
            e.HasIndex(x => new { x.WorkPlanId, x.OperationNumber }).IsUnique();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Operation_Number", "OperationNumber >= 1 AND OperationNumber <= 1000000");
                table.HasCheckConstraint("CK_Operation_SetupTime", "CAST(SetupTimeMinutes AS REAL) BETWEEN 0 AND 1000000");
                table.HasCheckConstraint("CK_Operation_RunTime", "CAST(TimePerPieceMinutes AS REAL) BETWEEN 0 AND 1000000");
                table.HasCheckConstraint("CK_Operation_Description", "length(trim(Description)) BETWEEN 1 AND 120");
                table.HasCheckConstraint("CK_Operation_Remarks", "Remarks IS NULL OR length(Remarks) <= 250");
            });

            // Don't allow deleting a work center that operations still point at.
            e.HasOne(x => x.WorkCenter)
             .WithMany(w => w.Operations)
             .HasForeignKey(x => x.WorkCenterId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ProductionOrder>(e =>
        {
            e.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.RoutingRevision).HasMaxLength(10).UseCollation("NOCASE");
            e.HasIndex(x => x.OrderNumber).IsUnique();

            // Restrict, not cascade: deleting a work plan must not silently remove
            // orders that were already released from it. Their snapshot is the
            // record of what the shop was told to build.
            e.HasOne(x => x.WorkPlan)
             .WithMany()
             .HasForeignKey(x => x.WorkPlanId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.RoutingCenters)
             .WithOne(r => r.ProductionOrder!)
             .HasForeignKey(r => r.ProductionOrderId)
             .OnDelete(DeleteBehavior.Cascade);

            // HasMaxLength is documentation on SQLite; only an explicit CHECK is
            // enforced, and this entity had none of them.
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_ProductionOrder_Quantity", "Quantity >= 1 AND Quantity <= 1000000");
                table.HasCheckConstraint("CK_ProductionOrder_Priority", "Priority BETWEEN 1 AND 5");
                table.HasCheckConstraint("CK_ProductionOrder_Status", "Status BETWEEN 0 AND 2");
                table.HasCheckConstraint("CK_ProductionOrder_OrderNumber", "length(trim(OrderNumber)) BETWEEN 1 AND 30");
                table.HasCheckConstraint("CK_ProductionOrder_Revision", "length(RoutingRevision) <= 10");
                table.HasCheckConstraint("CK_ProductionOrder_Dates", "\"DueLocal\" > \"ReleaseLocal\"");
            });
        });

        model.Entity<OrderRoutingCenter>(e =>
        {
            e.HasKey(x => new { x.ProductionOrderId, x.WorkCenterId });
            e.HasIndex(x => x.WorkCenterId);

            // The point of the table: the database itself refuses to delete a
            // work centre a released order still needs.
            e.HasOne(x => x.WorkCenter)
             .WithMany()
             .HasForeignKey(x => x.WorkCenterId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
