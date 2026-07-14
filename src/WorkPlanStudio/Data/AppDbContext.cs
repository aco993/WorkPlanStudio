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

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<WorkCenter>(e =>
        {
            // Server ownership/concurrency and production-capacity entities are
            // intentionally outside the schema of the offline browser demo.
            e.Ignore(x => x.OwnerId);
            e.Ignore(x => x.Version);
            e.Ignore(x => x.TimeZoneId);
            e.Ignore(x => x.CalendarShifts);
            e.Ignore(x => x.Downtimes);
            e.Ignore(x => x.SetupTransitions);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.CostCenter).HasMaxLength(20);
            e.Property(x => x.HourlyRate).HasColumnType("decimal(10,2)");
            e.HasIndex(x => x.Code).IsUnique();
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_WorkCenter_HourlyRate", "HourlyRate >= 0 AND HourlyRate <= 1000000");
                table.HasCheckConstraint("CK_WorkCenter_ParallelCapacity", "ParallelCapacity >= 1 AND ParallelCapacity <= 64");
                table.HasCheckConstraint("CK_WorkCenter_Code", "length(trim(Code)) BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_WorkCenter_Name", "length(trim(Name)) BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_WorkCenter_CostCenter", "length(CostCenter) <= 20");
            });
        });

        model.Entity<WorkPlan>(e =>
        {
            e.Ignore(x => x.OwnerId);
            e.Ignore(x => x.Version);
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
            e.Ignore(x => x.SetupFamily);
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
    }
}
