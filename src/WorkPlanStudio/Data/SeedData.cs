using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Data;

/// <summary>
/// Sample data inserted the first time the app runs (or after a reset), so the
/// portfolio demo is never empty. Content is generic, fictitious manufacturing
/// data — part numbers, machines and times are illustrative only.
/// </summary>
public static class SeedData
{
    public static void Apply(AppDbContext db)
    {
        if (db.WorkCenters.Any())
            return;

        var saw = new WorkCenter { Code = "SAW-10", Name = "Cut-off Saw", CostCenter = "CC-1000", HourlyRate = 42m };
        var lathe = new WorkCenter { Code = "CNC-200", Name = "CNC Turning Center", CostCenter = "CC-2000", HourlyRate = 78m };
        var mill = new WorkCenter { Code = "CNC-300", Name = "5-Axis Milling Center", CostCenter = "CC-2000", HourlyRate = 95m };
        var drill = new WorkCenter { Code = "DRL-120", Name = "Column Drill", CostCenter = "CC-1500", HourlyRate = 38m };
        var grind = new WorkCenter { Code = "GRD-400", Name = "Surface Grinder", CostCenter = "CC-3000", HourlyRate = 64m };
        var insp = new WorkCenter { Code = "QC-900", Name = "Quality Inspection", CostCenter = "CC-9000", HourlyRate = 55m };
        var asm = new WorkCenter { Code = "ASM-500", Name = "Manual Assembly", CostCenter = "CC-5000", HourlyRate = 48m };

        db.WorkCenters.AddRange(saw, lathe, mill, drill, grind, insp, asm);

        db.WorkPlans.AddRange(
            new WorkPlan
            {
                PlanNumber = "WP-1001",
                PartNumber = "SHAFT-08-114",
                PartName = "Drive shaft Ø20",
                Revision = "B",
                Status = WorkPlanStatus.Released,
                LotSize = 100,
                CreatedUtc = new DateTime(2026, 1, 14, 9, 30, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 3, 2, 13, 5, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Cut raw bar to length",       WorkCenter = saw,   SetupTimeMinutes = 10m, TimePerPieceMinutes = 0.8m },
                    new Operation { OperationNumber = 20, Description = "Turn outer diameter & faces",  WorkCenter = lathe, SetupTimeMinutes = 35m, TimePerPieceMinutes = 4.2m, Remarks = "Tolerance h7" },
                    new Operation { OperationNumber = 30, Description = "Mill keyway",                  WorkCenter = mill,  SetupTimeMinutes = 25m, TimePerPieceMinutes = 3.0m },
                    new Operation { OperationNumber = 40, Description = "Grind bearing seats",          WorkCenter = grind, SetupTimeMinutes = 20m, TimePerPieceMinutes = 2.5m },
                    new Operation { OperationNumber = 50, Description = "Final inspection",             WorkCenter = insp,  SetupTimeMinutes = 5m,  TimePerPieceMinutes = 1.5m },
                }
            },
            new WorkPlan
            {
                PlanNumber = "WP-1002",
                PartNumber = "BRKT-22-070",
                PartName = "Mounting bracket",
                Revision = "A",
                Status = WorkPlanStatus.Released,
                LotSize = 250,
                CreatedUtc = new DateTime(2026, 2, 3, 8, 0, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 4, 18, 10, 22, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Saw blank from sheet",  WorkCenter = saw,   SetupTimeMinutes = 8m,  TimePerPieceMinutes = 0.5m },
                    new Operation { OperationNumber = 20, Description = "Mill contour",          WorkCenter = mill,  SetupTimeMinutes = 30m, TimePerPieceMinutes = 2.1m },
                    new Operation { OperationNumber = 30, Description = "Drill mounting holes",  WorkCenter = drill, SetupTimeMinutes = 12m, TimePerPieceMinutes = 0.9m },
                    new Operation { OperationNumber = 40, Description = "Deburr & inspect",      WorkCenter = insp,  SetupTimeMinutes = 5m,  TimePerPieceMinutes = 0.7m },
                }
            },
            new WorkPlan
            {
                PlanNumber = "WP-1003",
                PartNumber = "HSG-31-205",
                PartName = "Gearbox housing",
                Revision = "C",
                Status = WorkPlanStatus.Released,
                LotSize = 40,
                CreatedUtc = new DateTime(2026, 5, 6, 14, 45, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 6, 1, 9, 15, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Rough mill casting",    WorkCenter = mill,  SetupTimeMinutes = 45m, TimePerPieceMinutes = 12.0m },
                    new Operation { OperationNumber = 20, Description = "Finish bores",          WorkCenter = mill,  SetupTimeMinutes = 40m, TimePerPieceMinutes = 8.5m, Remarks = "Ø62 H7" },
                    new Operation { OperationNumber = 30, Description = "Assemble bearings",     WorkCenter = asm,   SetupTimeMinutes = 15m, TimePerPieceMinutes = 6.0m },
                    new Operation { OperationNumber = 40, Description = "Leak & dimensional test", WorkCenter = insp, SetupTimeMinutes = 10m, TimePerPieceMinutes = 4.0m },
                }
            },
            new WorkPlan
            {
                PlanNumber = "WP-1004",
                PartNumber = "PIN-05-012",
                PartName = "Locating pin",
                Revision = "A",
                Status = WorkPlanStatus.Archived,
                LotSize = 500,
                CreatedUtc = new DateTime(2025, 11, 20, 7, 10, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 1, 9, 16, 40, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Cut bar stock",     WorkCenter = saw,   SetupTimeMinutes = 6m,  TimePerPieceMinutes = 0.3m },
                    new Operation { OperationNumber = 20, Description = "Turn to diameter",  WorkCenter = lathe, SetupTimeMinutes = 20m, TimePerPieceMinutes = 1.4m },
                    new Operation { OperationNumber = 30, Description = "Grind & polish",    WorkCenter = grind, SetupTimeMinutes = 18m, TimePerPieceMinutes = 1.1m, Remarks = "Ra 0.4" },
                    new Operation { OperationNumber = 40, Description = "Sample inspection", WorkCenter = insp,  SetupTimeMinutes = 5m,  TimePerPieceMinutes = 0.4m },
                }
            },
            new WorkPlan
            {
                PlanNumber = "WP-1005",
                PartNumber = "FLG-44-330",
                PartName = "Flange ring",
                Revision = "A",
                Status = WorkPlanStatus.Released,
                LotSize = 80,
                CreatedUtc = new DateTime(2026, 3, 11, 8, 20, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 5, 19, 11, 5, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Saw blank",           WorkCenter = saw,   SetupTimeMinutes = 8m,  TimePerPieceMinutes = 0.6m },
                    new Operation { OperationNumber = 20, Description = "Turn faces & bore",   WorkCenter = lathe, SetupTimeMinutes = 40m, TimePerPieceMinutes = 5.0m },
                    new Operation { OperationNumber = 30, Description = "Drill bolt circle",   WorkCenter = drill, SetupTimeMinutes = 15m, TimePerPieceMinutes = 1.2m },
                    new Operation { OperationNumber = 40, Description = "Final inspection",    WorkCenter = insp,  SetupTimeMinutes = 5m,  TimePerPieceMinutes = 0.8m },
                }
            },
            new WorkPlan
            {
                PlanNumber = "WP-1006",
                PartNumber = "SPN-17-088",
                PartName = "Drive spindle",
                Revision = "B",
                Status = WorkPlanStatus.Released,
                LotSize = 60,
                CreatedUtc = new DateTime(2026, 4, 2, 7, 45, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 5, 28, 9, 30, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Cut bar to length",   WorkCenter = saw,   SetupTimeMinutes = 10m, TimePerPieceMinutes = 0.7m },
                    new Operation { OperationNumber = 20, Description = "Turn & thread",       WorkCenter = lathe, SetupTimeMinutes = 45m, TimePerPieceMinutes = 6.5m, Remarks = "M20×1.5" },
                    new Operation { OperationNumber = 30, Description = "Grind journals",      WorkCenter = grind, SetupTimeMinutes = 25m, TimePerPieceMinutes = 3.0m },
                    new Operation { OperationNumber = 40, Description = "Inspect runout",      WorkCenter = insp,  SetupTimeMinutes = 6m,  TimePerPieceMinutes = 1.0m },
                }
            },
            new WorkPlan
            {
                PlanNumber = "WP-1007",
                PartNumber = "CVR-09-201",
                PartName = "Cover plate",
                Revision = "A",
                Status = WorkPlanStatus.Released,
                LotSize = 200,
                CreatedUtc = new DateTime(2026, 2, 24, 13, 10, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 5, 30, 15, 40, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Shear blank",         WorkCenter = saw,   SetupTimeMinutes = 7m,  TimePerPieceMinutes = 0.4m },
                    new Operation { OperationNumber = 20, Description = "Mill pocket",         WorkCenter = mill,  SetupTimeMinutes = 30m, TimePerPieceMinutes = 2.4m },
                    new Operation { OperationNumber = 30, Description = "Drill & tap holes",   WorkCenter = drill, SetupTimeMinutes = 12m, TimePerPieceMinutes = 0.8m },
                    new Operation { OperationNumber = 40, Description = "Deburr & inspect",    WorkCenter = insp,  SetupTimeMinutes = 4m,  TimePerPieceMinutes = 0.5m },
                }
            },
            new WorkPlan
            {
                PlanNumber = "WP-1008",
                PartNumber = "HUB-26-145",
                PartName = "Coupling hub",
                Revision = "C",
                Status = WorkPlanStatus.Released,
                LotSize = 120,
                CreatedUtc = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(2026, 6, 3, 8, 50, 0, DateTimeKind.Utc),
                Operations =
                {
                    new Operation { OperationNumber = 10, Description = "Saw billet",          WorkCenter = saw,   SetupTimeMinutes = 9m,  TimePerPieceMinutes = 0.6m },
                    new Operation { OperationNumber = 20, Description = "Turn outer profile",  WorkCenter = lathe, SetupTimeMinutes = 38m, TimePerPieceMinutes = 4.0m },
                    new Operation { OperationNumber = 30, Description = "Mill keyway & slots", WorkCenter = mill,  SetupTimeMinutes = 28m, TimePerPieceMinutes = 2.6m },
                    new Operation { OperationNumber = 40, Description = "Press bushings",      WorkCenter = asm,   SetupTimeMinutes = 12m, TimePerPieceMinutes = 2.0m },
                    new Operation { OperationNumber = 50, Description = "Final inspection",    WorkCenter = insp,  SetupTimeMinutes = 6m,  TimePerPieceMinutes = 0.9m },
                }
            }
        );

        // Orders are what actually gets scheduled, so the demo needs some. They
        // are saved first so the plans get their keys, then released - which is
        // what captures each routing snapshot.
        db.SaveChanges();
        ReleaseOrders(db);
    }

    /// <summary>
    /// One released order per released plan, with staggered releases and due
    /// dates so the schedule has real slack to work with rather than everything
    /// starting at zero and sharing one target.
    /// </summary>
    private static void ReleaseOrders(AppDbContext db)
    {
        if (db.ProductionOrders.Any())
            return;

        var horizon = new DateTime(2026, 6, 15, 6, 0, 0, DateTimeKind.Utc);

        // plan number -> (quantity, release offset in hours, due offset in hours, priority)
        var terms = new Dictionary<string, (int Quantity, int ReleaseHours, int DueHours, int Priority)>
        {
            ["WP-1001"] = (100, 0, 96, 3),
            ["WP-1002"] = (250, 0, 120, 1),
            ["WP-1003"] = (40, 8, 72, 5),
            ["WP-1005"] = (80, 0, 108, 1),
            ["WP-1006"] = (60, 16, 84, 4),
            ["WP-1007"] = (200, 8, 132, 1),
            ["WP-1008"] = (120, 24, 144, 2),
        };

        int sequence = 1001;
        foreach (var plan in db.WorkPlans.Include(p => p.Operations).ThenInclude(o => o.WorkCenter)
                     .Where(p => p.Status == WorkPlanStatus.Released)
                     .OrderBy(p => p.PlanNumber)
                     .ToList())
        {
            if (!terms.TryGetValue(plan.PlanNumber, out var t))
                continue;

            db.ProductionOrders.Add(new ProductionOrder
            {
                OrderNumber = $"PO-{sequence++}",
                WorkPlanId = plan.Id,
                Quantity = t.Quantity,
                ReleaseUtc = horizon.AddHours(t.ReleaseHours),
                DueUtc = horizon.AddHours(t.DueHours),
                Priority = t.Priority,
                Status = ProductionOrderStatus.Released,
                RoutingRevision = plan.Revision ?? "",
                RoutingSnapshotJson = RoutingSnapshot.Capture(plan).Serialize(),
                CreatedUtc = horizon,
                ModifiedUtc = horizon
            });
        }
    }
}
