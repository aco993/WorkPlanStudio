using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work centers.</summary>
public sealed class WorkCenterService
{
    private readonly BrowserDatabase _db;

    public WorkCenterService(BrowserDatabase db) => _db = db;

    public async Task<List<WorkCenter>> GetAllAsync()
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters
            .AsNoTracking()
            .OrderBy(w => w.Code)
            .ToListAsync();
    }

    /// <summary>Work centers that may be used as operation targets (active only).</summary>
    public async Task<List<WorkCenter>> GetActiveAsync()
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters
            .AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.Code)
            .ToListAsync();
    }

    public async Task<WorkCenter?> GetAsync(int id)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
    }

    public async Task<bool> CodeExistsAsync(string code, int exceptId = 0)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters.AnyAsync(w => w.Code == code && w.Id != exceptId);
    }

    public async Task SaveAsync(WorkCenter center)
    {
        await using var db = await _db.CreateContextAsync();

        if (center.Id == 0)
            db.WorkCenters.Add(center);
        else
            db.WorkCenters.Update(center);

        await db.SaveChangesAsync();
        await _db.PersistAsync();
    }

    /// <summary>How many operations currently reference this work center.</summary>
    public async Task<int> UsageCountAsync(int id)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.Operations.CountAsync(o => o.WorkCenterId == id);
    }

    /// <summary>Deletes a work center, unless operations still reference it.</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        await using var db = await _db.CreateContextAsync();

        if (await db.Operations.AnyAsync(o => o.WorkCenterId == id))
            return false;

        var center = await db.WorkCenters.FindAsync(id);
        if (center is null)
            return true;

        db.WorkCenters.Remove(center);
        await db.SaveChangesAsync();
        await _db.PersistAsync();
        return true;
    }
}
