using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work plans and their operations.</summary>
public sealed class WorkPlanService
{
    private const string NumberPrefix = "WP-";

    private readonly BrowserDatabase _db;
    private readonly IPermissionGuard _guard;

    public WorkPlanService(BrowserDatabase db, IPermissionGuard? guard = null)
    {
        _db = db;
        _guard = guard ?? AllowAllGuard.Instance;
    }

    public async Task<List<WorkPlan>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .OrderBy(w => w.PlanNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkPlan?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    /// <summary>The distinct part numbers already in use, for the editor's suggestions.</summary>
    /// <remarks>
    /// A part number is the key of a part master this app does not have, yet two
    /// plans for the same part must agree letter for letter — it is copied
    /// verbatim into every routing snapshot. Offering what is already there is
    /// what converges the spellings without inventing an entity.
    /// </remarks>
    public async Task<List<string>> GetPartNumbersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkPlans
            .Where(w => w.PartNumber != "")
            .Select(w => w.PartNumber)
            .Distinct()
            .OrderBy(number => number)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> PlanNumberExistsAsync(string planNumber, int exceptId = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.WorkPlans.AnyAsync(w => w.PlanNumber == planNumber && w.Id != exceptId, cancellationToken);
    }

    /// <summary>Suggests the next free plan number (e.g. "WP-1005").</summary>
    /// <remarks>
    /// It used to strip the prefix case-sensitively against a <c>NOCASE</c> unique
    /// index, so a plan stored as <c>wp-1009</c> parsed to zero, was ignored, and
    /// the app handed the user <c>WP-1009</c> — then refused to save it. It also
    /// never checked that its own suggestion was free.
    /// </remarks>
    public async Task<string> SuggestPlanNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        var taken = await db.WorkPlans.Select(w => w.PlanNumber).ToListAsync(cancellationToken);
        return NumberSuggestion.Next(NumberPrefix, taken);
    }

    public async Task<ApplicationResult<int>> CreateAsync(WorkPlan plan, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageMasterData, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        ArgumentNullException.ThrowIfNull(plan);
        Normalize(plan);

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                var centers = await db.WorkCenters.AsNoTracking().ToDictionaryAsync(center => center.Id, token);
                var issues = WorkPlanValidator.Validate(plan, centers);
                if (issues.Count > 0)
                    return ApplicationResult<Func<int>>.Validation(issues);

                if (await db.WorkPlans.AnyAsync(existing => existing.PlanNumber == plan.PlanNumber, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue(nameof(plan.PlanNumber), "Val_PlanNumberTaken"));

                plan.CreatedUtc = plan.ModifiedUtc = DateTime.UtcNow;
                foreach (var op in plan.Operations)
                    op.WorkCenter = null;

                db.WorkPlans.Add(plan);
                return ApplicationResult<Func<int>>.Success(() => plan.Id);
            },
            new ValidationIssue(nameof(plan.PlanNumber), "Val_PlanNumberTaken"),
            cancellationToken);
    }

    public async Task<ApplicationResult<int>> UpdateAsync(WorkPlan plan, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageMasterData, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        ArgumentNullException.ThrowIfNull(plan);
        Normalize(plan);

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                var existing = await db.WorkPlans
                    .Include(w => w.Operations)
                    .FirstOrDefaultAsync(w => w.Id == plan.Id, token);
                if (existing is null)
                    return ApplicationResult<Func<int>>.NotFound();

                var centers = await db.WorkCenters.AsNoTracking().ToDictionaryAsync(center => center.Id, token);
                var issues = WorkPlanValidator.Validate(plan, centers, existing.Status);
                if (issues.Count > 0)
                    return ApplicationResult<Func<int>>.Validation(issues);

                if (await db.WorkPlans.AnyAsync(
                        other => other.PlanNumber == plan.PlanNumber && other.Id != plan.Id, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue(nameof(plan.PlanNumber), "Val_PlanNumberTaken"));

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

                return ApplicationResult<Func<int>>.Success(() => existing.Id);
            },
            new ValidationIssue(nameof(plan.PlanNumber), "Val_PlanNumberTaken"),
            cancellationToken);
    }

    /// <summary>Deletes a work plan, unless a production order was raised from it.</summary>
    /// <remarks>
    /// The delete used to go straight to <c>SaveChangesAsync</c> with no
    /// <c>try</c>. Foreign keys are enforced and the relationship is
    /// <c>Restrict</c>, so deleting any of the seeded plans threw a
    /// <c>DbUpdateException</c> that nothing caught and the top-level error
    /// boundary replaced the entire application with "Something went wrong".
    /// </remarks>
    public async Task<ApplicationResult<int>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManageMasterData, cancellationToken))
            return ApplicationResult<int>.Forbidden();

        return await DatabaseMutation.RunAsync<int>(
            _db,
            async (db, token) =>
            {
                var plan = await db.WorkPlans.FirstOrDefaultAsync(w => w.Id == id, token);
                if (plan is null)
                    return ApplicationResult<Func<int>>.NotFound();

                if (await db.ProductionOrders.AnyAsync(order => order.WorkPlanId == id, token))
                    return ApplicationResult<Func<int>>.Conflict(
                        new ValidationIssue("WorkPlan", "Val_WorkPlanInUse"));

                db.WorkPlans.Remove(plan);
                return ApplicationResult<Func<int>>.Success(() => id);
            },
            new ValidationIssue("WorkPlan", "Val_WorkPlanInUse"),
            cancellationToken);
    }

    private static void Normalize(WorkPlan plan)
    {
        plan.PlanNumber = Text.Key(plan.PlanNumber);
        plan.PartNumber = Text.Key(plan.PartNumber);
        plan.PartName = plan.PartName?.Trim() ?? "";
        plan.Revision = string.IsNullOrWhiteSpace(plan.Revision) ? null : Text.Key(plan.Revision);
        plan.Operations ??= [];
        foreach (var operation in plan.Operations)
        {
            operation.Description = operation.Description?.Trim() ?? "";
            operation.Remarks = string.IsNullOrWhiteSpace(operation.Remarks) ? null : operation.Remarks.Trim();
        }
    }
}

/// <summary>
/// The "next free number" both master-data services offer.
/// </summary>
/// <remarks>
/// Shared because both got it wrong the same way: a case-sensitive prefix strip
/// against a case-insensitive unique index, no handling of anything that is not
/// exactly <c>PREFIX + digits</c>, and — the part that actually reached the user —
/// no check that the number it proposed was free.
/// </remarks>
public static class NumberSuggestion
{
    public static string Next(string prefix, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(taken);

        var used = new HashSet<string>(taken.Select(Text.Key), StringComparer.Ordinal);
        var highest = taken
            .Select(number => Text.Key(number))
            .Where(number => number.StartsWith(prefix, StringComparison.Ordinal))
            .Select(number => int.TryParse(number[prefix.Length..], out var value) ? value : 0)
            .DefaultIfEmpty(1000)
            .Max();

        var candidate = highest < 1000 ? 1000 : highest;
        do
        {
            candidate++;
        }
        while (used.Contains(prefix + candidate.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        return prefix + candidate.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
