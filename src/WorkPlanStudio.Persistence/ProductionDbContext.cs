using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Persistence;

public sealed class ProductionDbContext(DbContextOptions<ProductionDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();
    public DbSet<WorkPlan> WorkPlans => Set<WorkPlan>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<CalendarShift> CalendarShifts => Set<CalendarShift>();
    public DbSet<MachineDowntime> MachineDowntimes => Set<MachineDowntime>();
    public DbSet<SetupTransition> SetupTransitions => Set<SetupTransition>();
    public DbSet<ScheduleRun> ScheduleRuns => Set<ScheduleRun>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        model.Entity<WorkCenter>(entity =>
        {
            entity.Property(x => x.OwnerId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CostCenter).HasMaxLength(20);
            ConfigureDecimal(entity.Property(x => x.HourlyRate));
            entity.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OwnerId, x.Code }).IsUnique();
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_WorkCenter_Capacity", "\"ParallelCapacity\" BETWEEN 1 AND 64");
                table.HasCheckConstraint("CK_WorkCenter_Rate", "\"HourlyRate\" BETWEEN 0 AND 1000000");
            });
        });

        model.Entity<WorkPlan>(entity =>
        {
            entity.Property(x => x.OwnerId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.PlanNumber).HasMaxLength(20).IsRequired();
            entity.Property(x => x.PartNumber).HasMaxLength(40);
            entity.Property(x => x.PartName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Revision).HasMaxLength(10);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OwnerId, x.PlanNumber }).IsUnique();
            entity.HasMany(x => x.Operations).WithOne(x => x.WorkPlan)
                .HasForeignKey(x => x.WorkPlanId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Operation>(entity =>
        {
            entity.Property(x => x.Description).HasMaxLength(120).IsRequired();
            ConfigureDecimal(entity.Property(x => x.SetupTimeMinutes));
            ConfigureDecimal(entity.Property(x => x.TimePerPieceMinutes));
            entity.Property(x => x.SetupFamily).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Remarks).HasMaxLength(250);
            entity.HasIndex(x => new { x.WorkPlanId, x.OperationNumber }).IsUnique();
            entity.HasOne(x => x.WorkCenter).WithMany(x => x.Operations)
                .HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ProductionOrder>(entity =>
        {
            entity.Property(x => x.OwnerId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired();
            entity.Property(x => x.RoutingRevision).HasMaxLength(20).IsRequired();
            entity.Property(x => x.RoutingSnapshotJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OwnerId, x.OrderNumber }).IsUnique();
            entity.HasOne(x => x.WorkPlan).WithMany().HasForeignKey(x => x.WorkPlanId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_ProductionOrder_Quantity", "\"Quantity\" BETWEEN 1 AND 1000000");
                table.HasCheckConstraint("CK_ProductionOrder_Priority", "\"Priority\" BETWEEN 1 AND 10");
                table.HasCheckConstraint("CK_ProductionOrder_Dates", "\"DueUtc\" > \"ReleaseUtc\"");
            });
        });

        model.Entity<CalendarShift>(entity =>
        {
            entity.HasOne(x => x.WorkCenter).WithMany(x => x.CalendarShifts)
                .HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkCenterId, x.DayOfWeek, x.StartMinute }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_CalendarShift_Range", "\"StartMinute\" >= 0 AND \"EndMinute\" <= 1440 AND \"EndMinute\" > \"StartMinute\""));
        });

        model.Entity<MachineDowntime>(entity =>
        {
            entity.Property(x => x.Reason).HasMaxLength(200).IsRequired();
            entity.HasOne(x => x.WorkCenter).WithMany(x => x.Downtimes)
                .HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkCenterId, x.StartUtc, x.EndUtc });
            entity.ToTable(table => table.HasCheckConstraint("CK_Downtime_Range", "\"EndUtc\" > \"StartUtc\""));
        });

        model.Entity<SetupTransition>(entity =>
        {
            entity.Property(x => x.FromFamily).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ToFamily).HasMaxLength(40).IsRequired();
            entity.HasOne(x => x.WorkCenter).WithMany(x => x.SetupTransitions)
                .HasForeignKey(x => x.WorkCenterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkCenterId, x.FromFamily, x.ToFamily }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint("CK_SetupTransition_Duration", "\"DurationMinutes\" BETWEEN 0 AND 10080"));
        });

        model.Entity<ScheduleRun>(entity =>
        {
            entity.Property(x => x.OwnerId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.ParametersJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.ResultJson).HasColumnType("text");
            entity.Property(x => x.ErrorCode).HasMaxLength(100);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.OwnerId, x.CreatedUtc });
        });

        model.Entity<AuditEntry>(entity =>
        {
            entity.Property(x => x.OwnerId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(40).IsRequired();
            entity.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ChangesJson).HasColumnType("text");
            entity.HasIndex(x => new { x.OwnerId, x.OccurredUtc });
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries().Where(entry =>
                     entry.Entity is WorkPlan or WorkCenter or ProductionOrder or ScheduleRun &&
                     entry.State is EntityState.Added or EntityState.Modified))
        {
            var property = entry.Property("Version");
            property.CurrentValue = entry.State == EntityState.Added ? 1L : checked((long)(property.OriginalValue ?? 0L) + 1L);
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ConfigureDecimal(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<decimal> property)
    {
        if (Database.IsSqlite())
            property.HasConversion<double>();
        else
            property.HasPrecision(18, 2);
    }
}
