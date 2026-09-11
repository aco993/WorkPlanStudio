using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Api.Data;

/// <summary>
/// The server's store: the same domain entities the browser app owns, plus the
/// Identity tables and the refresh-token ledger.
/// <para>
/// The entity classes are shared with the browser app (linked source, see the
/// project file); this mapping is not. Two reasons it stays separate rather than
/// being lifted out of <c>AppDbContext</c>: this one has to inherit
/// <see cref="IdentityDbContext{TUser}" />, and it adds an optimistic-concurrency
/// token that only makes sense where more than one person can write. A browser
/// database with exactly one writer needs neither.
/// </para>
/// <para>
/// The token is a shadow property, so the entity classes stay ignorant of it —
/// the alternative would have been a column on a class the browser app also
/// compiles, for a problem the browser app does not have.
/// </para>
/// </summary>
public sealed class ApiDbContext : IdentityDbContext<ApiUser>
{
    /// <summary>Name of the shadow optimistic-concurrency column on the master-data tables.</summary>
    public const string ConcurrencyStamp = "ConcurrencyStamp";

    /// <summary>Creates the context.</summary>
    /// <param name="options">Provider and connection, supplied by the host.</param>
    public ApiDbContext(DbContextOptions<ApiDbContext> options) : base(options) { }

    /// <summary>Routings.</summary>
    public DbSet<WorkPlan> WorkPlans => Set<WorkPlan>();

    /// <summary>Routing steps.</summary>
    public DbSet<Operation> Operations => Set<Operation>();

    /// <summary>Machines, cells and manual stations.</summary>
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();

    /// <summary>Orders that are actually scheduled.</summary>
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();

    /// <summary>One-off closed periods per work center.</summary>
    public DbSet<WorkCenterAbsence> WorkCenterAbsences => Set<WorkCenterAbsence>();

    /// <summary>The single plant-settings row.</summary>
    public DbSet<PlantSettings> PlantSettings => Set<PlantSettings>();

    /// <summary>Issued refresh tokens, hashed.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>
    /// Gives an entity a fresh version and states the version the caller believed
    /// it was writing over, so the generated UPDATE carries the check in its
    /// WHERE clause and a losing writer gets a <see cref="DbUpdateConcurrencyException"/>
    /// rather than silently overwriting.
    /// </summary>
    /// <typeparam name="TEntity">The tracked entity type.</typeparam>
    /// <param name="entry">The tracked entry being written.</param>
    /// <param name="expected">
    /// The stamp the caller read. Null or empty means "I did not check" and skips
    /// the comparison — used for creates and for the internal seeder, never for a
    /// client update, where the endpoint requires the value.
    /// </param>
    public static void Restamp<TEntity>(EntityEntry<TEntity> entry, string? expected)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entry);

        var property = entry.Property<string>(ConcurrencyStamp);
        if (!string.IsNullOrEmpty(expected))
            property.OriginalValue = expected;

        // Always a new value, so the row is written even when nothing else
        // changed; otherwise a no-op update would skip the version check.
        property.CurrentValue = NewStamp();
    }

    /// <summary>A fresh opaque version token.</summary>
    public static string NewStamp() => Guid.NewGuid().ToString("N");

    /// <summary>Reads the current version token of a tracked entity.</summary>
    /// <typeparam name="TEntity">The tracked entity type.</typeparam>
    /// <param name="entry">The tracked entry.</param>
    /// <returns>The stamp, or an empty string when the row has none yet.</returns>
    public static string StampOf<TEntity>(EntityEntry<TEntity> entry)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Property<string>(ConcurrencyStamp).CurrentValue ?? "";
    }

    /// <summary>
    /// Stamps every new row on the way in, so no insert can leave a versioned
    /// table without a version. Updates are stamped deliberately by the endpoint
    /// that also states the expected value — a safety net here must not quietly
    /// paper over a missing concurrency check there.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of state entries written.</returns>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added || entry.Metadata.FindProperty(ConcurrencyStamp) is null)
                continue;

            var property = entry.Property(ConcurrencyStamp);
            if (property.CurrentValue is not string value || value.Length == 0)
                property.CurrentValue = NewStamp();
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);
        base.OnModelCreating(model);

        model.Entity<WorkCenter>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.CostCenter).HasMaxLength(20);
            e.Property(x => x.HourlyRate).HasColumnType("decimal(10,2)");
            e.Property(x => x.ShiftPatternKey).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            Versioned(e);
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
            Versioned(e);
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

            e.HasOne(x => x.WorkCenter)
             .WithMany(w => w.Operations)
             .HasForeignKey(x => x.WorkCenterId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ProductionOrder>(e =>
        {
            e.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.RoutingRevision).HasMaxLength(10);
            e.HasIndex(x => x.OrderNumber).IsUnique();
            Versioned(e);

            e.HasOne(x => x.WorkPlan)
             .WithMany()
             .HasForeignKey(x => x.WorkPlanId)
             .OnDelete(DeleteBehavior.Restrict);

            e.ToTable(t => t.HasCheckConstraint("CK_ProductionOrder_Quantity", "Quantity >= 1"));
            e.ToTable(t => t.HasCheckConstraint("CK_ProductionOrder_Priority", "Priority BETWEEN 1 AND 5"));
        });

        model.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReplacedByHash).HasMaxLength(64);
            e.Property(x => x.RevokedReason).HasMaxLength(60);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.ExpiresUtc });
            e.HasOne<ApiUser>()
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void Versioned<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class =>
        entity.Property<string>(ConcurrencyStamp)
              .HasMaxLength(32)
              .IsRequired()
              .IsConcurrencyToken()
              .HasDefaultValue("");
}
