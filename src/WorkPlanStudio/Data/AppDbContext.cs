using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Data;

/// <summary>
/// EF Core context for the whole app. On WebAssembly this talks to a SQLite
/// database that lives in the browser's virtual file system.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<WorkPlan> WorkPlans => Set<WorkPlan>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<WorkCenterAbsence> WorkCenterAbsences => Set<WorkCenterAbsence>();
    public DbSet<PlantSettings> PlantSettings => Set<PlantSettings>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<WorkCenter>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.CostCenter).HasMaxLength(20);
            e.Property(x => x.HourlyRate).HasColumnType("decimal(10,2)");
            e.Property(x => x.ShiftPatternKey).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_WorkCenter_HourlyRate", "HourlyRate >= 0 AND HourlyRate <= 1000000");
                table.HasCheckConstraint("CK_WorkCenter_ParallelCapacity", "ParallelCapacity >= 1 AND ParallelCapacity <= 64");
                table.HasCheckConstraint("CK_WorkCenter_Code", "length(trim(Code)) BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_WorkCenter_Name", "length(trim(Name)) BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_WorkCenter_CostCenter", "length(CostCenter) <= 20");
                table.HasCheckConstraint("CK_WorkCenter_ShiftPattern", "length(trim(ShiftPatternKey)) BETWEEN 1 AND 40");
            });

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
            });
        });

        model.Entity<WorkPlan>(e =>
        {
            e.Property(x => x.PlanNumber).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.PartNumber).HasMaxLength(40);
            e.Property(x => x.PartName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Revision).HasMaxLength(10);
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
            e.Property(x => x.SetupTimeMinutes).HasColumnType("decimal(10,2)");
            e.Property(x => x.TimePerPieceMinutes).HasColumnType("decimal(10,2)");
            e.Property(x => x.Remarks).HasMaxLength(250);
            e.HasIndex(x => new { x.WorkPlanId, x.OperationNumber }).IsUnique();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Operation_Number", "OperationNumber >= 1 AND OperationNumber <= 1000000");
                table.HasCheckConstraint("CK_Operation_SetupTime", "SetupTimeMinutes >= 0 AND SetupTimeMinutes <= 1000000");
                table.HasCheckConstraint("CK_Operation_RunTime", "TimePerPieceMinutes >= 0 AND TimePerPieceMinutes <= 1000000");
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
            e.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired();
            e.Property(x => x.RoutingRevision).HasMaxLength(10);
            e.HasIndex(x => x.OrderNumber).IsUnique();

            // Restrict, not cascade: deleting a work plan must not silently remove
            // orders that were already released from it. Their snapshot is the
            // record of what the shop was told to build.
            e.HasOne(x => x.WorkPlan)
             .WithMany()
             .HasForeignKey(x => x.WorkPlanId)
             .OnDelete(DeleteBehavior.Restrict);

            e.ToTable(t => t.HasCheckConstraint("CK_ProductionOrder_Quantity", "Quantity >= 1"));
            e.ToTable(t => t.HasCheckConstraint("CK_ProductionOrder_Priority", "Priority BETWEEN 1 AND 5"));
        });

    }
}
