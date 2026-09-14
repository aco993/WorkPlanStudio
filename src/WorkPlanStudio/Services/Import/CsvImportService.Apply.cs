using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services.Import;

public sealed partial class CsvImportService
{
    /// <summary>
    /// Resolves the staged rows against the database and, when asked, stages the
    /// changes on the context.
    /// </summary>
    /// <remarks>
    /// One method for both the dry run and the commit, on purpose. A preview
    /// computed by separate code is a second implementation of the import that
    /// nothing keeps in step, and the first time the two disagree the user has
    /// already pressed the button. Here <paramref name="write"/> only decides
    /// whether the entities are attached to the context; every decision, every
    /// validator call and every count is the same code either way, and the commit
    /// compares its own counts with the preview's before it lets the transaction
    /// stand.
    /// </remarks>
    private static async Task<Resolution> ApplyAsync(
        AppDbContext db,
        StagedRows staged,
        ImportOptions options,
        bool write,
        CancellationToken cancellationToken)
    {
        var resolution = new Resolution();
        resolution.Rejected.AddRange(staged.Rejected);

        switch (options.Kind)
        {
            case ImportEntityKind.CostCenters:
                await ApplyCostCentersAsync(db, staged, options, write, resolution, cancellationToken);
                break;
            case ImportEntityKind.WorkCenters:
                await ApplyWorkCentersAsync(db, staged, options, write, resolution, cancellationToken);
                break;
            case ImportEntityKind.WorkPlans:
                await ApplyWorkPlansAsync(db, staged, options, write, resolution, cancellationToken);
                break;
            case ImportEntityKind.ProductionOrders:
                await ApplyOrdersAsync(db, staged, options, write, resolution, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        // A rejected line belongs in the preview too. Listing only the rows that
        // would succeed makes a file with a hundred bad rows look like a short
        // file rather than a broken one, and leaves the reason with nothing on
        // the table to point at.
        var listed = resolution.Rows.Select(row => row.Line).ToHashSet();
        foreach (var line in resolution.Rejected.Select(issue => issue.Line).Distinct())
            if (listed.Add(line))
                resolution.Rows.Add(new ImportRowSummary(line, ImportRowAction.Reject, "", ""));

        resolution.Rows.Sort((left, right) => left.Line.CompareTo(right.Line));
        resolution.Rejected.Sort((left, right) => left.Line.CompareTo(right.Line));
        return resolution;
    }

    private static async Task ApplyCostCentersAsync(
        AppDbContext db,
        StagedRows staged,
        ImportOptions options,
        bool write,
        Resolution resolution,
        CancellationToken cancellationToken)
    {
        var existing = await db.CostCenters.ToDictionaryAsync(
            costCenter => costCenter.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pending in staged.CostCenters)
        {
            if (Duplicate(resolution, seen, pending.Code, pending.Line, staged, "Code"))
                continue;

            var candidate = new CostCenter
            {
                Code = pending.Code,
                Name = pending.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(pending.Description) ? null : pending.Description.Trim(),
                IsActive = pending.IsActive
            };

            if (Invalid(resolution, staged, pending.Line, CostCenterValidator.Validate(candidate)))
                continue;

            var detail = candidate.Name;
            if (!existing.TryGetValue(pending.Code, out var row))
            {
                resolution.Creates++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Create, pending.Code, detail));
                if (write)
                {
                    db.CostCenters.Add(candidate);
                    existing[pending.Code] = candidate;
                }

                continue;
            }

            if (!options.UpdateExisting)
            {
                resolution.Skips++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Skip, pending.Code, detail));
                continue;
            }

            resolution.Updates++;
            resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Update, pending.Code, detail));
            resolution.Warnings.Add(ImportIssue.At(
                pending.Line, Column(staged, "Code"), "Import_Warn_Overwrite", pending.Code));

            if (!write)
                continue;

            row.Code = candidate.Code;
            row.Name = candidate.Name;
            row.Description = candidate.Description;
            row.IsActive = candidate.IsActive;
        }
    }

    private static async Task ApplyWorkCentersAsync(
        AppDbContext db,
        StagedRows staged,
        ImportOptions options,
        bool write,
        Resolution resolution,
        CancellationToken cancellationToken)
    {
        var existing = await db.WorkCenters.ToDictionaryAsync(
            center => center.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var costCenters = await db.CostCenters.ToDictionaryAsync(
            costCenter => costCenter.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // The two questions the work-centre form asks before letting a machine be
        // switched off. A file must not be a way around them.
        var neededByOrders = (await db.OrderRoutingCenters
            .Where(routing => routing.ProductionOrder!.Status == ProductionOrderStatus.Released)
            .Select(routing => routing.WorkCenterId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        var neededByPlans = (await db.Operations
            .Where(operation => operation.WorkPlan!.Status == WorkPlanStatus.Released)
            .Select(operation => operation.WorkCenterId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pending in staged.WorkCenters)
        {
            if (Duplicate(resolution, seen, pending.Code, pending.Line, staged, "Code"))
                continue;

            CostCenter? costCenter = null;
            if (pending.CostCenterCode.Length > 0 && !costCenters.TryGetValue(pending.CostCenterCode, out costCenter))
            {
                if (!options.CreateMissingCostCenters)
                {
                    resolution.Rejected.Add(ImportIssue.At(
                        pending.Line,
                        Column(staged, "CostCenterCode"),
                        "Import_Error_UnknownCostCenter",
                        pending.CostCenterCode));
                    continue;
                }

                // Its name starts out as its code, exactly as the schema-6 upgrade
                // does: the file never carried one, and inventing a friendlier
                // label would be inventing master data Controlling owns.
                costCenter = new CostCenter { Code = pending.CostCenterCode, Name = pending.CostCenterCode };
                costCenters[pending.CostCenterCode] = costCenter;
                resolution.NewCostCenters++;
                resolution.Warnings.Add(ImportIssue.At(
                    pending.Line,
                    Column(staged, "CostCenterCode"),
                    "Import_Warn_NewCostCenter",
                    pending.CostCenterCode));

                if (write)
                    db.CostCenters.Add(costCenter);
            }

            var candidate = new WorkCenter
            {
                Code = pending.Code,
                Name = pending.Name.Trim(),
                HourlyRate = pending.HourlyRate,
                ParallelCapacity = pending.ParallelCapacity,
                ShiftPatternKey = pending.ShiftPatternKey,
                IsActive = pending.IsActive
            };

            if (Invalid(resolution, staged, pending.Line, WorkCenterValidator.Validate(candidate)))
                continue;

            var found = existing.TryGetValue(pending.Code, out var row);
            if (found && row!.IsActive && !candidate.IsActive)
            {
                var blocked = neededByOrders.Contains(row.Id) ? "Val_WorkCenterOrderUse"
                    : neededByPlans.Contains(row.Id) ? "Val_WorkCenterReleasedUse"
                    : null;
                if (blocked is not null)
                {
                    resolution.Rejected.Add(ImportIssue.At(pending.Line, Column(staged, "IsActive"), blocked));
                    continue;
                }
            }

            var detail = candidate.Name;
            if (!found)
            {
                resolution.Creates++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Create, pending.Code, detail));
                if (write)
                {
                    Attach(candidate, costCenter);
                    db.WorkCenters.Add(candidate);
                    existing[pending.Code] = candidate;
                }

                continue;
            }

            if (!options.UpdateExisting)
            {
                resolution.Skips++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Skip, pending.Code, detail));
                continue;
            }

            resolution.Updates++;
            resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Update, pending.Code, detail));
            resolution.Warnings.Add(ImportIssue.At(
                pending.Line, Column(staged, "Code"), "Import_Warn_Overwrite", pending.Code));

            if (!write)
                continue;

            row!.Code = candidate.Code;
            row.Name = candidate.Name;
            row.HourlyRate = candidate.HourlyRate;
            row.ParallelCapacity = candidate.ParallelCapacity;
            row.ShiftPatternKey = candidate.ShiftPatternKey;
            row.IsActive = candidate.IsActive;
            Attach(row, costCenter);
        }
    }

    /// <summary>
    /// Points a work centre at its cost centre by key when there is one, and by
    /// navigation when the cost centre is itself being created in this import and
    /// has no key yet.
    /// </summary>
    private static void Attach(WorkCenter center, CostCenter? costCenter)
    {
        if (costCenter is null)
        {
            center.CostCenter = null;
            center.CostCenterId = null;
        }
        else if (costCenter.Id != 0)
        {
            center.CostCenter = null;
            center.CostCenterId = costCenter.Id;
        }
        else
        {
            center.CostCenter = costCenter;
        }
    }

    private static async Task ApplyWorkPlansAsync(
        AppDbContext db,
        StagedRows staged,
        ImportOptions options,
        bool write,
        Resolution resolution,
        CancellationToken cancellationToken)
    {
        var centersByCode = await db.WorkCenters.ToDictionaryAsync(
            center => center.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var centersById = centersByCode.Values.ToDictionary(center => center.Id);
        var existing = await db.WorkPlans
            .Include(plan => plan.Operations)
            .ToDictionaryAsync(plan => plan.PlanNumber, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var orderCounts = await db.ProductionOrders
            .GroupBy(order => order.WorkPlanId)
            .Select(group => new { WorkPlanId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.WorkPlanId, row => row.Count, cancellationToken);

        foreach (var pending in staged.Plans)
        {
            var duplicate = pending.Operations
                .GroupBy(operation => operation.Number)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                resolution.Rejected.Add(ImportIssue.At(
                    duplicate.Skip(1).First().Line,
                    Column(staged, "OperationNumber"),
                    "Import_Error_DuplicateOperation",
                    duplicate.Key,
                    pending.PlanNumber));
                continue;
            }

            var unknown = pending.Operations.FirstOrDefault(
                operation => !centersByCode.ContainsKey(operation.WorkCenterCode));
            if (unknown is not null)
            {
                resolution.Rejected.Add(ImportIssue.At(
                    unknown.Line,
                    Column(staged, "WorkCenterCode"),
                    "Import_Error_UnknownWorkCenter",
                    unknown.WorkCenterCode));
                continue;
            }

            // Explicit, because a file has no order of its own: the shop floor runs
            // operations by number, not by the order a spreadsheet happened to save
            // them in.
            var operations = pending.Operations
                .OrderBy(operation => operation.Number)
                .Select(operation => new Operation
                {
                    OperationNumber = operation.Number,
                    Description = operation.Description.Trim(),
                    WorkCenterId = centersByCode[operation.WorkCenterCode].Id,
                    SetupTimeMinutes = operation.SetupMinutes,
                    TimePerPieceMinutes = operation.RunMinutes,
                    Remarks = string.IsNullOrWhiteSpace(operation.Remarks) ? null : operation.Remarks.Trim()
                })
                .ToList();

            var found = existing.TryGetValue(pending.PlanNumber, out var row);
            var candidate = new WorkPlan
            {
                Id = found ? row!.Id : 0,
                PlanNumber = pending.PlanNumber,
                PartNumber = pending.PartNumber,
                PartName = pending.PartName.Trim(),
                Revision = pending.Revision,
                Status = pending.Status,
                LotSize = pending.LotSize,
                Operations = operations
            };

            if (Invalid(resolution, staged, pending.Line, WorkPlanValidator.Validate(candidate, centersById, found ? row!.Status : null)))
                continue;

            var detail = $"{candidate.PartName} · {operations.Count}";
            if (!found)
            {
                resolution.Creates++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Create, pending.PlanNumber, detail));
                if (write)
                {
                    candidate.CreatedUtc = candidate.ModifiedUtc = DateTime.UtcNow;
                    db.WorkPlans.Add(candidate);
                    existing[pending.PlanNumber] = candidate;
                }

                continue;
            }

            if (!options.UpdateExisting)
            {
                resolution.Skips++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Skip, pending.PlanNumber, detail));
                continue;
            }

            resolution.Updates++;
            resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Update, pending.PlanNumber, detail));
            resolution.Warnings.Add(ImportIssue.At(
                pending.Line, Column(staged, "PlanNumber"), "Import_Warn_Overwrite", pending.PlanNumber));

            // Replacing the operations of a routing that orders were raised from is
            // exactly the "silent restructure" this app exists to prevent. It is
            // allowed - the same edit is allowed in the editor - but it is never
            // quiet: the count of affected orders is on the preview before the
            // button is pressed.
            if (orderCounts.TryGetValue(row!.Id, out var orders) && orders > 0)
                resolution.Warnings.Add(ImportIssue.At(
                    pending.Line, Column(staged, "PlanNumber"), "Import_Warn_PlanHasOrders", pending.PlanNumber, orders));

            if (!write)
                continue;

            row.PlanNumber = candidate.PlanNumber;
            row.PartNumber = candidate.PartNumber;
            row.PartName = candidate.PartName;
            row.Revision = candidate.Revision;
            row.Status = candidate.Status;
            row.LotSize = candidate.LotSize;
            row.ModifiedUtc = DateTime.UtcNow;
            db.Operations.RemoveRange(row.Operations);
            row.Operations = operations;
        }
    }

    private static async Task ApplyOrdersAsync(
        AppDbContext db,
        StagedRows staged,
        ImportOptions options,
        bool write,
        Resolution resolution,
        CancellationToken cancellationToken)
    {
        var plans = await db.WorkPlans.ToDictionaryAsync(
            plan => plan.PlanNumber, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var existing = await db.ProductionOrders.ToDictionaryAsync(
            order => order.OrderNumber, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pending in staged.Orders)
        {
            if (Duplicate(resolution, seen, pending.OrderNumber, pending.Line, staged, "OrderNumber"))
                continue;

            if (!plans.TryGetValue(pending.PlanNumber, out var plan))
            {
                resolution.Rejected.Add(ImportIssue.At(
                    pending.Line, Column(staged, "PlanNumber"), "Imp_WorkPlanNotFound", pending.PlanNumber));
                continue;
            }

            if (plan.Status != WorkPlanStatus.Released)
            {
                resolution.Rejected.Add(ImportIssue.At(
                    pending.Line, Column(staged, "PlanNumber"), "Val_PlanNotReleased"));
                continue;
            }

            var found = existing.TryGetValue(pending.OrderNumber, out var row);

            // A released order's routing snapshot is what the shop floor is
            // building to. The form refuses to edit it; a file does not get to.
            if (found && row!.Status != ProductionOrderStatus.Draft)
            {
                resolution.Rejected.Add(ImportIssue.At(
                    pending.Line, Column(staged, "OrderNumber"), "Val_OrderNotDraft"));
                continue;
            }

            var candidate = new ProductionOrder
            {
                Id = found ? row!.Id : 0,
                OrderNumber = pending.OrderNumber,
                WorkPlanId = plan.Id,
                Quantity = pending.Quantity,
                ReleaseLocal = pending.ReleaseLocal,
                DueLocal = pending.DueLocal,
                Priority = pending.Priority,
                Status = ProductionOrderStatus.Draft
            };

            if (Invalid(resolution, staged, pending.Line, ProductionOrderValidator.Validate(candidate)))
                continue;

            var detail = $"{plan.PlanNumber} · {candidate.Quantity}";
            if (!found)
            {
                resolution.Creates++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Create, pending.OrderNumber, detail));
                if (write)
                {
                    candidate.CreatedUtc = candidate.ModifiedUtc = DateTime.UtcNow;
                    db.ProductionOrders.Add(candidate);
                    existing[pending.OrderNumber] = candidate;
                }

                continue;
            }

            if (!options.UpdateExisting)
            {
                resolution.Skips++;
                resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Skip, pending.OrderNumber, detail));
                continue;
            }

            resolution.Updates++;
            resolution.Rows.Add(new ImportRowSummary(pending.Line, ImportRowAction.Update, pending.OrderNumber, detail));
            resolution.Warnings.Add(ImportIssue.At(
                pending.Line, Column(staged, "OrderNumber"), "Import_Warn_Overwrite", pending.OrderNumber));

            if (!write)
                continue;

            row!.WorkPlanId = candidate.WorkPlanId;
            row.Quantity = candidate.Quantity;
            row.ReleaseLocal = candidate.ReleaseLocal;
            row.DueLocal = candidate.DueLocal;
            row.Priority = candidate.Priority;
            row.ModifiedUtc = DateTime.UtcNow;
        }
    }

    // ----- shared decisions ----------------------------------------------

    private static bool Duplicate(
        Resolution resolution,
        Dictionary<string, int> seen,
        string key,
        int line,
        StagedRows staged,
        string field)
    {
        if (!seen.TryGetValue(key, out var first))
        {
            seen[key] = line;
            return false;
        }

        resolution.Rejected.Add(ImportIssue.At(line, Column(staged, field), "Import_Error_DuplicateKey", key, first));
        return true;
    }

    /// <summary>
    /// Turns the validators' own issues into rows of the rejected report, keeping
    /// the resource key and its arguments so the message is the same sentence the
    /// form would have shown.
    /// </summary>
    private static bool Invalid(
        Resolution resolution,
        StagedRows staged,
        int line,
        IReadOnlyList<ValidationIssue> issues)
    {
        if (issues.Count == 0)
            return false;

        foreach (var issue in issues)
            resolution.Rejected.Add(new ImportIssue(line, Column(staged, issue.Field), issue.MessageKey, issue.Arguments));

        return true;
    }

    /// <summary>The header cell a field came from, when the file has one for it.</summary>
    private static string? Column(StagedRows staged, string field) =>
        staged.Columns.GetValueOrDefault(field);
}
