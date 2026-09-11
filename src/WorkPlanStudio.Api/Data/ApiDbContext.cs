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

    /// <summary>
    /// The column type for a <c>decimal</c>, kept identical to the browser
    /// context's.
    /// <para>
    /// <c>TEXT</c> and not <c>decimal(10,2)</c>: SQLite derives affinity from
    /// substrings of the declared type, and <c>decimal(10,2)</c> contains none of
    /// INT/CHAR/CLOB/TEXT/BLOB/REAL/FLOA/DOUB, so it lands on NUMERIC — which
    /// converts EF's text to an IEEE-754 double on the way in. Rates and minutes
    /// then round-trip through a binary float while the column claims two decimal
    /// places it does not enforce either. This server stores the same rows the
    /// browser does and the client pulls them straight into that database, so a
    /// divergence here is a divergence in the numbers themselves.
    /// </para>
    /// <para>
    /// The trap it leaves is why the range CHECKs below cast: on a TEXT column
    /// <c>HourlyRate &lt;= 1000000</c> is a string comparison, and
    /// <c>'9' &gt; '1000'</c> — silently inverting the check. The scale the old
    /// type claimed is enforced where it can be, in the shared validators.
    /// </para>
    /// </summary>
    private const string DecimalColumnType = "TEXT";

    /// <summary>Creates the context.</summary>
    /// <param name="options">Provider and connection, supplied by the host.</param>
    public ApiDbContext(DbContextOptions<ApiDbContext> options) : base(options) { }

    /// <summary>Routings.</summary>
    public DbSet<WorkPlan> WorkPlans => Set<WorkPlan>();

    /// <summary>Routing steps.</summary>
    public DbSet<Operation> Operations => Set<Operation>();

    /// <summary>Machines, cells and manual stations.</summary>
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();

    /// <summary>The accounting units work centers book against.</summary>
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();

    /// <summary>Orders that are actually scheduled.</summary>
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();

    /// <summary>The work centers each released order's frozen routing still needs.</summary>
    public DbSet<OrderRoutingCenter> OrderRoutingCenters => Set<OrderRoutingCenter>();

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

        model.Entity<CostCenter>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(250);
            e.HasIndex(x => x.Code).IsUnique();
            Versioned(e);
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
            Versioned(e);
            e.ToTable(table =>
            {
                table.HasCheckConstraint("CK_WorkCenter_HourlyRate", "CAST(HourlyRate AS REAL) BETWEEN 0 AND 1000000");
                table.HasCheckConstraint("CK_WorkCenter_ParallelCapacity", "ParallelCapacity >= 1 AND ParallelCapacity <= 64");
                table.HasCheckConstraint("CK_WorkCenter_Code", "length(trim(Code)) BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_WorkCenter_Name", "length(trim(Name)) BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_WorkCenter_ShiftPattern", "length(trim(ShiftPatternKey)) BETWEEN 1 AND 40");
            });

            // Restrict, not cascade: retiring a cost center must not take the
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
            });
        });

        model.Entity<WorkPlan>(e =>
        {
            e.Property(x => x.PlanNumber).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.PartNumber).HasMaxLength(40).UseCollation("NOCASE");
            e.Property(x => x.PartName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Revision).HasMaxLength(10).UseCollation("NOCASE");
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
            Versioned(e);

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
            // enforced, and this entity had almost none of them.
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
            // work center a released order still needs.
            e.HasOne(x => x.WorkCenter)
             .WithMany()
             .HasForeignKey(x => x.WorkCenterId)
             .OnDelete(DeleteBehavior.Restrict);
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
