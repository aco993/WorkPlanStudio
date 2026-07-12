using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work plans and their operations.</summary>
public sealed class WorkPlanService
{
    private readonly BrowserDatabase _db;

    public WorkPlanService(BrowserDatabase db) => _db = db;

    public async Task<List<WorkPlan>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .OrderBy(w => w.PlanNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkPlan?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<bool> PlanNumberExistsAsync(string planNumber, int exceptId = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans.AnyAsync(w => w.PlanNumber == planNumber && w.Id != exceptId, cancellationToken);
    }

    /// <summary>Suggests the next free plan number (e.g. "WP-1005").</summary>
    public async Task<string> SuggestPlanNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        var numbers = await db.WorkPlans.Select(w => w.PlanNumber).ToListAsync(cancellationToken);
        var highest = numbers
            .Select(n => int.TryParse(n.Replace("WP-", ""), out var value) ? value : 0)
            .DefaultIfEmpty(1000)
            .Max();
        return $"WP-{highest + 1}";
    }

    public async Task<ApplicationResult<int>> CreateAsync(WorkPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Normalize(plan);
        await using (var db = await _db.CreateContextAsync())
        {
            var centers = await db.WorkCenters.AsNoTracking().ToDictionaryAsync(center => center.Id, cancellationToken);
            var issues = WorkPlanValidator.Validate(plan, centers);
            if (issues.Count > 0)
                return ApplicationResult<int>.Validation(issues);

            if (await db.WorkPlans.AnyAsync(existing => existing.PlanNumber == plan.PlanNumber, cancellationToken))
                return ApplicationResult<int>.Conflict(new ValidationIssue(nameof(plan.PlanNumber), "Val_PlanNumberTaken"));

            plan.CreatedUtc = plan.ModifiedUtc = DateTime.UtcNow;
            foreach (var op in plan.Operations)
                op.WorkCenter = null;

            db.WorkPlans.Add(plan);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return ApplicationResult<int>.PersistenceFailed();
            }
        }

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<int>.Success(plan.Id)
            : ApplicationResult<int>.PersistenceFailed();
    }

    public async Task<ApplicationResult<int>> UpdateAsync(WorkPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Normalize(plan);
        await using (var db = await _db.CreateContextAsync())
        {
            var existing = await db.WorkPlans
                .Include(w => w.Operations)
                .FirstOrDefaultAsync(w => w.Id == plan.Id, cancellationToken);
            if (existing is null)
                return ApplicationResult<int>.NotFound();

            var centers = await db.WorkCenters.AsNoTracking().ToDictionaryAsync(center => center.Id, cancellationToken);
            var issues = WorkPlanValidator.Validate(plan, centers, existing.Status);
            if (issues.Count > 0)
                return ApplicationResult<int>.Validation(issues);

            if (await db.WorkPlans.AnyAsync(
                    other => other.PlanNumber == plan.PlanNumber && other.Id != plan.Id,
                    cancellationToken))
                return ApplicationResult<int>.Conflict(new ValidationIssue(nameof(plan.PlanNumber), "Val_PlanNumberTaken"));

            existing.PlanNumber = plan.PlanNumber;
            existing.PartNumber = plan.PartNumber;
            existing.PartName = plan.PartName;
            existing.Revision = plan.Revision;
            existing.Status = plan.Status;
            existing.LotSize = plan.LotSize;
            existing.ModifiedUtc = DateTime.UtcNow;

            db.Operations.RemoveRange(existing.Operations);
            existing.Operations = plan.Operations.Select(o => new Operation
            {
                OperationNumber = o.OperationNumber,
                Description = o.Description,
                WorkCenterId = o.WorkCenterId,
                SetupTimeMinutes = o.SetupTimeMinutes,
                TimePerPieceMinutes = o.TimePerPieceMinutes,
                Remarks = o.Remarks
            }).ToList();

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return ApplicationResult<int>.PersistenceFailed();
            }
        }

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<int>.Success(plan.Id)
            : ApplicationResult<int>.PersistenceFailed();
    }

    public async Task<ApplicationResult<int>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using (var db = await _db.CreateContextAsync())
        {
            var plan = await db.WorkPlans.FindAsync([id], cancellationToken);
            if (plan is null)
                return ApplicationResult<int>.NotFound();

            db.WorkPlans.Remove(plan);
            await db.SaveChangesAsync(cancellationToken);
        }

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<int>.Success(id)
            : ApplicationResult<int>.PersistenceFailed();
    }

    private static void Normalize(WorkPlan plan)
    {
        plan.PlanNumber = plan.PlanNumber?.Trim() ?? "";
        plan.PartNumber = plan.PartNumber?.Trim() ?? "";
        plan.PartName = plan.PartName?.Trim() ?? "";
        plan.Revision = string.IsNullOrWhiteSpace(plan.Revision) ? null : plan.Revision.Trim();
        plan.Operations ??= [];
        foreach (var operation in plan.Operations)
        {
            operation.Description = operation.Description?.Trim() ?? "";
            operation.Remarks = string.IsNullOrWhiteSpace(operation.Remarks) ? null : operation.Remarks.Trim();
        }
    }
}
