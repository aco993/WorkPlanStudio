using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for cost centres.</summary>
public sealed class CostCenterService
{
    private readonly BrowserDatabase _db;
    private readonly IPermissionGuard _guard;

    public CostCenterService(BrowserDatabase db, IPermissionGuard? guard = null)
    {
        _db = db;
        _guard = guard ?? AllowAllGuard.Instance;
    }

    public async Task<List<CostCenter>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.CostCenters.AsNoTracking().OrderBy(c => c.Code).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// What a picker offers: every active cost centre, plus the one the row being
    /// edited already points at even if it has been retired.
    /// </summary>
    /// <remarks>
    /// Dropping a referenced-but-inactive entry would silently rewrite the row the
    /// moment anyone opened it and pressed Save — the picker would have no option
    /// matching the stored value, so the binding would fall back to "none".
    /// </remarks>
    public async Task<List<CostCenter>> GetSelectableAsync(int? keep = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.CostCenters
            .AsNoTracking()
            .Where(c => c.IsActive || (keep != null && c.Id == keep))
            .OrderBy(c => c.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task<CostCenter?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.CostCenters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    /// <summary>How many work centres book against each cost centre, in one grouped query.</summary>
    public async Task<Dictionary<int, int>> GetUsageCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkCenters
            .Where(w => w.CostCenterId != null)
            .GroupBy(w => w.CostCenterId!.Value)
            .Select(group => new { CostCenterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.CostCenterId, row => row.Count, cancellationToken);
    }

    public async Task<ApplicationResult<int>> SaveAsync(CostCenter costCenter, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageMasterData, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        ArgumentNullException.ThrowIfNull(costCenter);
        Normalize(costCenter);

        var issues = CostCenterValidator.Validate(costCenter);
        if (issues.Count > 0)
            return ApplicationResult<int>.Validation(issues);

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                if (await db.CostCenters.AnyAsync(
                        other => other.Code == costCenter.Code && other.Id != costCenter.Id, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue(nameof(costCenter.Code), "Val_CostCenterCodeTaken"));

                if (costCenter.Id == 0)
                {
                    db.CostCenters.Add(costCenter);
                    return ApplicationResult<Func<int>>.Success(() => costCenter.Id);
                }

                var existing = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == costCenter.Id, token);
                if (existing is null)
                    return ApplicationResult<Func<int>>.NotFound();

                existing.Code = costCenter.Code;
                existing.Name = costCenter.Name;
                existing.Description = costCenter.Description;
                existing.IsActive = costCenter.IsActive;
                return ApplicationResult<Func<int>>.Success(() => existing.Id);
            },
            new ValidationIssue(nameof(costCenter.Code), "Val_CostCenterCodeTaken"),
            cancellationToken);
    }

    /// <summary>Deletes a cost centre, unless work centres still book against it.</summary>
    public async Task<ApplicationResult<int>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageMasterData, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                var costCenter = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id, token);
                if (costCenter is null)
                    return ApplicationResult<Func<int>>.NotFound();

                // The pre-check gives the planner a sentence; the foreign key
                // behind it is what actually makes the rule true.
                if (await db.WorkCenters.AnyAsync(w => w.CostCenterId == id, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue("CostCenter", "Val_CostCenterInUse"));

                db.CostCenters.Remove(costCenter);
                return ApplicationResult<Func<int>>.Success(() => id);
            },
            new ValidationIssue("CostCenter", "Val_CostCenterInUse"),
            cancellationToken);
    }

    private static void Normalize(CostCenter costCenter)
    {
        costCenter.Code = Text.Key(costCenter.Code);
        costCenter.Name = costCenter.Name?.Trim() ?? "";
        costCenter.Description = string.IsNullOrWhiteSpace(costCenter.Description)
            ? null
            : costCenter.Description.Trim();
    }
}
