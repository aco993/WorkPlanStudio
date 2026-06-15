using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work plans and their operations.</summary>
public sealed class WorkPlanService
{
    private readonly BrowserDatabase _db;

    public WorkPlanService(BrowserDatabase db) => _db = db;

    public async Task<List<WorkPlan>> GetAllAsync()
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .OrderBy(w => w.PlanNumber)
            .ToListAsync();
    }

    public async Task<WorkPlan?> GetAsync(int id)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id);
    }

    public async Task<bool> PlanNumberExistsAsync(string planNumber, int exceptId = 0)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans.AnyAsync(w => w.PlanNumber == planNumber && w.Id != exceptId);
    }

    /// <summary>Suggests the next free plan number (e.g. "WP-1005").</summary>
    public async Task<string> SuggestPlanNumberAsync()
    {
        await using var db = await _db.CreateContextAsync();
        var numbers = await db.WorkPlans.Select(w => w.PlanNumber).ToListAsync();
        var highest = numbers
            .Select(n => int.TryParse(n.Replace("WP-", ""), out var value) ? value : 0)
            .DefaultIfEmpty(1000)
            .Max();
        return $"WP-{highest + 1}";
    }

    public async Task<int> CreateAsync(WorkPlan plan)
    {
        await using var db = await _db.CreateContextAsync();

        plan.CreatedUtc = plan.ModifiedUtc = DateTime.UtcNow;
        // Operations reference work centers by id only; clear the navigation so
        // EF does not try to insert the (already existing) work centers again.
        foreach (var op in plan.Operations)
            op.WorkCenter = null;

        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        await _db.PersistAsync();
        return plan.Id;
    }

    public async Task UpdateAsync(WorkPlan plan)
    {
        await using var db = await _db.CreateContextAsync();

        var existing = await db.WorkPlans
            .Include(w => w.Operations)
            .FirstOrDefaultAsync(w => w.Id == plan.Id);
        if (existing is null)
            return;

        existing.PlanNumber = plan.PlanNumber;
        existing.PartNumber = plan.PartNumber;
        existing.PartName = plan.PartName;
        existing.Revision = plan.Revision;
        existing.Status = plan.Status;
        existing.LotSize = plan.LotSize;
        existing.ModifiedUtc = DateTime.UtcNow;

        // Replace the operation list wholesale — simplest reliable strategy for
        // a small editable grid.
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

        await db.SaveChangesAsync();
        await _db.PersistAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _db.CreateContextAsync();
        var plan = await db.WorkPlans.FindAsync(id);
        if (plan is null)
            return;

        db.WorkPlans.Remove(plan);
        await db.SaveChangesAsync();
        await _db.PersistAsync();
    }
}
