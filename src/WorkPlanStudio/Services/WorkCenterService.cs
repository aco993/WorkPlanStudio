using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work centers.</summary>
public sealed class WorkCenterService
{
    private readonly BrowserDatabase _db;
    private readonly IPermissionGuard _guard;

    public WorkCenterService(BrowserDatabase db, IPermissionGuard? guard = null)
    {
        _db = db;
        _guard = guard ?? AllowAllGuard.Instance;
    }

    public async Task<List<WorkCenter>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkCenters
            .Include(w => w.CostCenter)
            .AsNoTracking()
            .OrderBy(w => w.Code)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Work centers that may be used as operation targets (active only).</summary>
    public async Task<List<WorkCenter>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkCenters
            .AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkCenter?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkCenters
            .Include(w => w.CostCenter)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<bool> CodeExistsAsync(string code, int exceptId = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkCenters.AnyAsync(w => w.Code == code && w.Id != exceptId, cancellationToken);
    }

    public async Task<ApplicationResult<int>> SaveAsync(WorkCenter center, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageMasterData, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        ArgumentNullException.ThrowIfNull(center);
        Normalize(center);
        var issues = WorkCenterValidator.Validate(center);
        if (issues.Count > 0)
            return ApplicationResult<int>.Validation(issues);

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                if (await db.WorkCenters.AnyAsync(
                        existing => existing.Code == center.Code && existing.Id != center.Id, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue(nameof(center.Code), "Val_CodeTaken"));

                if (center.CostCenterId is { } costCenterId
                    && !await db.CostCenters.AnyAsync(c => c.Id == costCenterId, token))
                    return ApplicationResult<Func<int>>.Validation(
                        [new ValidationIssue(nameof(center.CostCenterId), "Val_CostCenterMissing")]);

                if (center.Id == 0)
                {
                    db.WorkCenters.Add(center);
                    return ApplicationResult<Func<int>>.Success(() => center.Id);
                }

                var existingCenter = await db.WorkCenters.FirstOrDefaultAsync(w => w.Id == center.Id, token);
                if (existingCenter is null)
                    return ApplicationResult<Func<int>>.NotFound();

                if (existingCenter.IsActive && !center.IsActive)
                {
                    var blocked = await DeactivationBlockedAsync(db, center.Id, token);
                    if (blocked is not null)
                        return ApplicationResult<Func<int>>.Conflict(blocked);
                }

                existingCenter.Code = center.Code;
                existingCenter.Name = center.Name;
                existingCenter.CostCenterId = center.CostCenterId;
                existingCenter.HourlyRate = center.HourlyRate;
                existingCenter.ParallelCapacity = center.ParallelCapacity;
                existingCenter.ShiftPatternKey = center.ShiftPatternKey;
                existingCenter.IsActive = center.IsActive;
                return ApplicationResult<Func<int>>.Success(() => existingCenter.Id);
            },
            new ValidationIssue(nameof(center.Code), "Val_CodeTaken"),
            cancellationToken);
    }

    /// <summary>
    /// Why a work centre may not be deactivated, or <c>null</c> when it may.
    /// </summary>
    /// <remarks>
    /// The guard used to ask "does a <em>released work plan</em> use it", which is
    /// the wrong question and was routable around in three clicks: set the plan
    /// back to Draft, deactivate the centre, and a released shop-floor order
    /// silently disappeared from the schedule as an <c>InactiveWorkCenter</c>
    /// preparation error. The question that matters is whether a released
    /// <em>order</em> still needs it — the plan check stays as well, because a
    /// released routing is also a commitment.
    /// </remarks>
    private static async Task<ValidationIssue?> DeactivationBlockedAsync(
        AppDbContext db,
        int workCenterId,
        CancellationToken cancellationToken)
    {
        if (await db.OrderRoutingCenters.AnyAsync(
                r => r.WorkCenterId == workCenterId
                     && r.ProductionOrder!.Status == ProductionOrderStatus.Released,
                cancellationToken))
            return new ValidationIssue(nameof(WorkCenter.IsActive), "Val_WorkCenterOrderUse");

        if (await db.Operations.AnyAsync(
                operation => operation.WorkCenterId == workCenterId
                             && operation.WorkPlan!.Status == WorkPlanStatus.Released,
                cancellationToken))
            return new ValidationIssue(nameof(WorkCenter.IsActive), "Val_WorkCenterReleasedUse");

        return null;
    }

    /// <summary>How many operations currently reference this work center.</summary>
    public async Task<int> UsageCountAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.Operations.CountAsync(o => o.WorkCenterId == id, cancellationToken);
    }

    /// <summary>
    /// All usage counts in one grouped SQL query — operations plus the released
    /// orders whose frozen routing still names the centre. The second half was
    /// invisible before: an edit that removed the last operation made the delete
    /// button light up for a machine a live order depends on.
    /// </summary>
    public async Task<Dictionary<int, int>> GetUsageCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);

        var counts = await db.Operations
            .GroupBy(operation => operation.WorkCenterId)
            .Select(group => new { WorkCenterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.WorkCenterId, row => row.Count, cancellationToken);

        var released = await db.OrderRoutingCenters
            .Where(r => r.ProductionOrder!.Status == ProductionOrderStatus.Released)
            .GroupBy(r => r.WorkCenterId)
            .Select(group => new { WorkCenterId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        foreach (var row in released)
            counts[row.WorkCenterId] = counts.GetValueOrDefault(row.WorkCenterId) + row.Count;

        return counts;
    }

    /// <summary>Deletes a work center, unless operations or released orders still reference it.</summary>
    public async Task<ApplicationResult<int>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageMasterData, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                var center = await db.WorkCenters.FirstOrDefaultAsync(w => w.Id == id, token);
                if (center is null)
                    return ApplicationResult<Func<int>>.NotFound();

                if (await db.Operations.AnyAsync(o => o.WorkCenterId == id, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue("WorkCenter", "Val_WorkCenterInUse"));

                if (await db.OrderRoutingCenters.AnyAsync(r => r.WorkCenterId == id, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue("WorkCenter", "Val_WorkCenterOrderUse"));

                db.WorkCenters.Remove(center);
                return ApplicationResult<Func<int>>.Success(() => id);
            },
            new ValidationIssue("WorkCenter", "Val_WorkCenterInUse"),
            cancellationToken);
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
        if (!await _guard.CanAsync(Permissions.ManageAbsences, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        ArgumentNullException.ThrowIfNull(absence);
        absence.Label = absence.Label?.Trim() ?? "";
        absence.Start = PlantTime.Wall(absence.Start);
        absence.End = PlantTime.Wall(absence.End);

        var issues = WorkCenterValidator.ValidateAbsence(absence);
        if (issues.Count > 0)
            return ApplicationResult<int>.Validation(issues);

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                if (!await db.WorkCenters.AnyAsync(c => c.Id == absence.WorkCenterId, token))
                    return ApplicationResult<Func<int>>.NotFound();

                db.WorkCenterAbsences.Add(new WorkCenterAbsence
                {
                    WorkCenterId = absence.WorkCenterId,
                    Start = absence.Start,
                    End = absence.End,
                    Kind = absence.Kind,
                    Label = absence.Label
                });

                return ApplicationResult<Func<int>>.Success(() => absence.WorkCenterId);
            },
            cancellationToken: cancellationToken);
    }

    public async Task<ApplicationResult<int>> RemoveAbsenceAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageAbsences, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                var absence = await db.WorkCenterAbsences.FirstOrDefaultAsync(a => a.Id == id, token);
                if (absence is null)
                    return ApplicationResult<Func<int>>.NotFound();

                db.WorkCenterAbsences.Remove(absence);
                return ApplicationResult<Func<int>>.Success(() => id);
            },
            cancellationToken: cancellationToken);
    }

    private static void Normalize(WorkCenter center)
    {
        center.Code = Text.Key(center.Code);
        center.Name = center.Name?.Trim() ?? "";
        center.ShiftPatternKey = center.ShiftPatternKey?.Trim() ?? "";
        center.CostCenter = null;   // the id is the truth; a stale navigation must not be re-inserted
    }
}
