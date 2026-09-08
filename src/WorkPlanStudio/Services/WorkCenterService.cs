using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work centers.</summary>
public sealed class WorkCenterService
{
    private readonly BrowserDatabase _db;

    public WorkCenterService(BrowserDatabase db) => _db = db;

    public async Task<List<WorkCenter>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters
            .AsNoTracking()
            .OrderBy(w => w.Code)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Work centers that may be used as operation targets (active only).</summary>
    public async Task<List<WorkCenter>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters
            .AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkCenter?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<bool> CodeExistsAsync(string code, int exceptId = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters.AnyAsync(w => w.Code == code && w.Id != exceptId, cancellationToken);
    }

    public async Task<ApplicationResult<int>> SaveAsync(WorkCenter center, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(center);
        Normalize(center);
        var issues = WorkCenterValidator.Validate(center);
        if (issues.Count > 0)
            return ApplicationResult<int>.Validation(issues);

        await using (var db = await _db.CreateContextAsync())
        {
            if (await db.WorkCenters.AnyAsync(
                    existing => existing.Code == center.Code && existing.Id != center.Id,
                    cancellationToken))
                return ApplicationResult<int>.Conflict(new ValidationIssue(nameof(center.Code), "Val_CodeTaken"));

            if (center.Id == 0)
            {
                db.WorkCenters.Add(center);
            }
            else
            {
                var existing = await db.WorkCenters.FindAsync([center.Id], cancellationToken);
                if (existing is null)
                    return ApplicationResult<int>.NotFound();

                if (existing.IsActive && !center.IsActive && await db.Operations.AnyAsync(
                        operation => operation.WorkCenterId == center.Id &&
                                     operation.WorkPlan!.Status == WorkPlanStatus.Released,
                        cancellationToken))
                    return ApplicationResult<int>.Conflict(
                        new ValidationIssue(nameof(center.IsActive), "Val_WorkCenterReleasedUse"));

                existing.Code = center.Code;
                existing.Name = center.Name;
                existing.CostCenter = center.CostCenter;
                existing.HourlyRate = center.HourlyRate;
                existing.ParallelCapacity = center.ParallelCapacity;
                existing.ShiftPatternKey = center.ShiftPatternKey;
                existing.IsActive = center.IsActive;
            }

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
            ? ApplicationResult<int>.Success(center.Id)
            : ApplicationResult<int>.PersistenceFailed();
    }

    /// <summary>How many operations currently reference this work center.</summary>
    public async Task<int> UsageCountAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.Operations.CountAsync(o => o.WorkCenterId == id, cancellationToken);
    }

    /// <summary>All usage counts in one grouped SQL query.</summary>
    public async Task<Dictionary<int, int>> GetUsageCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.Operations
            .GroupBy(operation => operation.WorkCenterId)
            .Select(group => new { WorkCenterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.WorkCenterId, row => row.Count, cancellationToken);
    }

    /// <summary>Deletes a work center, unless operations still reference it.</summary>
    public async Task<ApplicationResult<int>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using (var db = await _db.CreateContextAsync())
        {
            if (await db.Operations.AnyAsync(o => o.WorkCenterId == id, cancellationToken))
                return ApplicationResult<int>.Conflict(new ValidationIssue("WorkCenter", "Val_WorkCenterInUse"));

            var center = await db.WorkCenters.FindAsync([id], cancellationToken);
            if (center is null)
                return ApplicationResult<int>.NotFound();

            db.WorkCenters.Remove(center);
            await db.SaveChangesAsync(cancellationToken);
        }

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<int>.Success(id)
            : ApplicationResult<int>.PersistenceFailed();
    }

    // ----- absences -----

    /// <summary>Every absence, oldest first, with its work center loaded for display.</summary>
    public async Task<List<WorkCenterAbsence>> GetAbsencesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkCenterAbsences
            .Include(a => a.WorkCenter)
            .AsNoTracking()
            .OrderBy(a => a.Start)
            .ThenBy(a => a.WorkCenterId)
            .ToListAsync(cancellationToken);
    }

    public async Task<ApplicationResult<int>> AddAbsenceAsync(WorkCenterAbsence absence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(absence);
        absence.Label = absence.Label?.Trim() ?? "";

        var issues = WorkCenterValidator.ValidateAbsence(absence);
        if (issues.Count > 0)
            return ApplicationResult<int>.Validation(issues);

        await using (var db = await _db.CreateContextAsync(cancellationToken))
        {
            if (!await db.WorkCenters.AnyAsync(c => c.Id == absence.WorkCenterId, cancellationToken))
                return ApplicationResult<int>.NotFound();

            db.WorkCenterAbsences.Add(new WorkCenterAbsence
            {
                WorkCenterId = absence.WorkCenterId,
                Start = absence.Start,
                End = absence.End,
                Kind = absence.Kind,
                Label = absence.Label
            });

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
            ? ApplicationResult<int>.Success(absence.WorkCenterId)
            : ApplicationResult<int>.PersistenceFailed();
    }

    public async Task<ApplicationResult<int>> RemoveAbsenceAsync(int id, CancellationToken cancellationToken = default)
    {
        await using (var db = await _db.CreateContextAsync(cancellationToken))
        {
            var absence = await db.WorkCenterAbsences.FindAsync([id], cancellationToken);
            if (absence is null)
                return ApplicationResult<int>.NotFound();

            db.WorkCenterAbsences.Remove(absence);
            await db.SaveChangesAsync(cancellationToken);
        }

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<int>.Success(id)
            : ApplicationResult<int>.PersistenceFailed();
    }

    private static void Normalize(WorkCenter center)
    {
        center.Code = center.Code?.Trim() ?? "";
        center.Name = center.Name?.Trim() ?? "";
        center.CostCenter = center.CostCenter?.Trim() ?? "";
        center.ShiftPatternKey = center.ShiftPatternKey?.Trim() ?? "";
    }
}
