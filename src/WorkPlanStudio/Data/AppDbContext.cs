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
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.CostCenter).HasMaxLength(20);
            e.Property(x => x.HourlyRate).HasColumnType("decimal(10,2)");
            e.HasIndex(x => x.Code).IsUnique();
        });

        model.Entity<WorkPlan>(e =>
        {
            e.Property(x => x.PlanNumber).HasMaxLength(20).IsRequired();
            e.Property(x => x.PartNumber).HasMaxLength(40);
            e.Property(x => x.PartName).HasMaxLength(120);
            e.Property(x => x.Revision).HasMaxLength(10);
            e.HasIndex(x => x.PlanNumber).IsUnique();

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

            // Don't allow deleting a work center that operations still point at.
            e.HasOne(x => x.WorkCenter)
             .WithMany(w => w.Operations)
             .HasForeignKey(x => x.WorkCenterId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
