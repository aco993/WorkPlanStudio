using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Api.Data;

/// <summary>One seeded account, read from configuration.</summary>
public sealed class SeedUser
{
    /// <summary>The account name to create if it is missing.</summary>
    public string UserName { get; set; } = "";

    /// <summary>
    /// The initial password. It comes from configuration — user secrets, an
    /// environment variable, the Development settings file — and never from this
    /// source tree, because a password committed to a public repository is not a
    /// password.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>One of <see cref="WorkspaceRoles"/>.</summary>
    public string Role { get; set; } = WorkspaceRoles.Guest;
}

/// <summary>Options for what start-up creates when the database is empty.</summary>
public sealed class SeedOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Seed";

    /// <summary>Whether the sample plant is inserted into an empty database.</summary>
    public bool SampleData { get; set; } = true;

    /// <summary>Accounts to create if they do not exist yet. Empty in a real deployment.</summary>
    public IList<SeedUser> Users { get; } = [];
}

/// <summary>
/// Brings a fresh database up to something a reviewer can sign into and schedule.
/// <para>
/// Roles are always ensured, because the authorization model is meaningless
/// without them. Accounts and sample rows are not: both are configuration, and a
/// deployment that wants an empty plant and its own users gets one by saying so.
/// Nothing here overwrites data that already exists.
/// </para>
/// </summary>
public static class DemoSeeder
{
    /// <summary>Creates the three roles if they are missing.</summary>
    /// <param name="roles">Identity's role store.</param>
    public static async Task EnsureRolesAsync(RoleManager<IdentityRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        foreach (var role in WorkspaceRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));
        }
    }

    /// <summary>Creates the configured accounts if they are missing.</summary>
    /// <param name="users">Identity's user store.</param>
    /// <param name="seed">The accounts to ensure.</param>
    /// <param name="log">Sink for which accounts were created. Passwords are never written to it.</param>
    /// <exception cref="InvalidOperationException">A configured account is unusable (bad role, or a password Identity refuses).</exception>
    public static async Task EnsureUsersAsync(UserManager<ApiUser> users, IEnumerable<SeedUser> seed, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(log);

        foreach (var candidate in seed)
        {
            if (string.IsNullOrWhiteSpace(candidate.UserName) || string.IsNullOrEmpty(candidate.Password))
                throw new InvalidOperationException(
                    $"{SeedOptions.SectionName}:Users contains an entry without a user name or password.");

            if (!WorkspaceRoles.IsKnown(candidate.Role))
                throw new InvalidOperationException(
                    $"{SeedOptions.SectionName}:Users entry '{candidate.UserName}' has an unknown role '{candidate.Role}'.");

            if (await users.FindByNameAsync(candidate.UserName) is not null)
                continue;

            var user = new ApiUser
            {
                UserName = candidate.UserName,
                DisplayName = candidate.UserName,
                EmailConfirmed = true
            };

            var created = await users.CreateAsync(user, candidate.Password);
            if (!created.Succeeded)
                throw new InvalidOperationException(
                    $"Could not create the seeded account '{candidate.UserName}': " +
                    string.Join(", ", created.Errors.Select(error => error.Code)));

            await users.AddToRoleAsync(user, candidate.Role);
            log.LogInformation("Seeded account {UserName} with role {Role}", candidate.UserName, candidate.Role);
        }
    }

    /// <summary>
    /// Inserts the sample plant — machines, two routings and the orders released
    /// from them — into an empty database. Fictitious throughout.
    /// </summary>
    /// <param name="db">The store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>True when rows were inserted; false when the database was not empty.</returns>
    public static async Task<bool> EnsureSampleDataAsync(ApiDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await db.WorkCenters.AnyAsync(cancellationToken))
            return false;

        var saw = new WorkCenter { Code = "SAW-10", Name = "Cut-off Saw", CostCenter = "CC-1000", HourlyRate = 42m, ShiftPatternKey = ShiftPatterns.OneShift.Key };
        var lathe = new WorkCenter { Code = "CNC-200", Name = "CNC Turning Center", CostCenter = "CC-2000", HourlyRate = 78m, ShiftPatternKey = ShiftPatterns.TwoShift.Key };
        var mill = new WorkCenter { Code = "CNC-300", Name = "5-Axis Milling Center", CostCenter = "CC-2000", HourlyRate = 95m, ShiftPatternKey = ShiftPatterns.ThreeShift.Key };
        var grind = new WorkCenter { Code = "GRD-400", Name = "Surface Grinder", CostCenter = "CC-3000", HourlyRate = 64m, ShiftPatternKey = ShiftPatterns.OneShift.Key };
        var inspect = new WorkCenter { Code = "QC-900", Name = "Quality Inspection", CostCenter = "CC-9000", HourlyRate = 55m, ShiftPatternKey = ShiftPatterns.OneShift.Key };

        db.WorkCenters.AddRange(saw, lathe, mill, grind, inspect);
        db.PlantSettings.Add(new PlantSettings { ModifiedUtc = new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc) });

        grind.Absences.Add(new WorkCenterAbsence
        {
            Start = new DateTime(2026, 6, 2, 7, 0, 0, DateTimeKind.Unspecified),
            End = new DateTime(2026, 6, 2, 15, 30, 0, DateTimeKind.Unspecified),
            Kind = AbsenceKind.Maintenance,
            Label = "Spindle service"
        });

        var created = new DateTime(2026, 1, 14, 9, 30, 0, DateTimeKind.Utc);

        var shaft = new WorkPlan
        {
            PlanNumber = "WP-1001",
            PartNumber = "SHAFT-08-114",
            PartName = "Drive shaft 20 mm",
            Revision = "B",
            Status = WorkPlanStatus.Released,
            LotSize = 100,
            CreatedUtc = created,
            ModifiedUtc = created,
            Operations =
            {
                new Operation { OperationNumber = 10, Description = "Cut raw bar to length", WorkCenter = saw, SetupTimeMinutes = 10m, TimePerPieceMinutes = 0.8m },
                new Operation { OperationNumber = 20, Description = "Turn outer diameter and faces", WorkCenter = lathe, SetupTimeMinutes = 35m, TimePerPieceMinutes = 4.2m, Remarks = "Tolerance h7" },
                new Operation { OperationNumber = 30, Description = "Mill keyway", WorkCenter = mill, SetupTimeMinutes = 25m, TimePerPieceMinutes = 3.0m },
                new Operation { OperationNumber = 40, Description = "Grind bearing seats", WorkCenter = grind, SetupTimeMinutes = 20m, TimePerPieceMinutes = 2.5m },
                new Operation { OperationNumber = 50, Description = "Final inspection", WorkCenter = inspect, SetupTimeMinutes = 5m, TimePerPieceMinutes = 1.5m }
            }
        };

        var bracket = new WorkPlan
        {
            PlanNumber = "WP-1002",
            PartNumber = "BRKT-22-009",
            PartName = "Mounting bracket",
            Revision = "A",
            Status = WorkPlanStatus.Released,
            LotSize = 250,
            CreatedUtc = created,
            ModifiedUtc = created,
            Operations =
            {
                new Operation { OperationNumber = 10, Description = "Saw blank", WorkCenter = saw, SetupTimeMinutes = 8m, TimePerPieceMinutes = 0.5m },
                new Operation { OperationNumber = 20, Description = "Mill contour", WorkCenter = mill, SetupTimeMinutes = 40m, TimePerPieceMinutes = 2.2m },
                new Operation { OperationNumber = 30, Description = "Deburr and inspect", WorkCenter = inspect, SetupTimeMinutes = 5m, TimePerPieceMinutes = 0.9m }
            }
        };

        db.WorkPlans.AddRange(shaft, bracket);
        await db.SaveChangesAsync(cancellationToken);

        // Released orders, so the demo has something to schedule on first sight.
        // Release freezes the routing exactly as the endpoint would.
        var release = new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc);
        db.ProductionOrders.AddRange(
            NewOrder("PO-1001", shaft, quantity: 60, release, release.AddDays(6), priority: 3),
            NewOrder("PO-1002", bracket, quantity: 120, release.AddHours(4), release.AddDays(5), priority: 1),
            NewOrder("PO-1003", shaft, quantity: 25, release.AddDays(1), release.AddDays(4), priority: 5));

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ProductionOrder NewOrder(
        string number, WorkPlan plan, int quantity, DateTime release, DateTime due, int priority) =>
        new()
        {
            OrderNumber = number,
            WorkPlanId = plan.Id,
            Quantity = quantity,
            ReleaseUtc = release,
            DueUtc = due,
            Priority = priority,
            Status = ProductionOrderStatus.Released,
            RoutingRevision = plan.Revision ?? "",
            RoutingSnapshotJson = RoutingSnapshot.Capture(plan).Serialize(),
            CreatedUtc = release,
            ModifiedUtc = release
        };
}
