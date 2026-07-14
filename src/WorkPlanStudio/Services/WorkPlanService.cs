using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work plans and their operations.</summary>
public sealed class WorkPlanService
{
    private readonly BrowserDatabase _db;
    private readonly BackendState? _backend;
    private readonly ServerSession? _server;

    public WorkPlanService(BrowserDatabase db) => _db = db;

    public WorkPlanService(BrowserDatabase db, BackendState backend, ServerSession server)
    {
        _db = db;
        _backend = backend;
        _server = server;
    }

    private bool UseServer => _backend?.Mode == BackendMode.Server;

    public async Task<List<WorkPlan>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (UseServer)
            return await GetRemotePlansAsync("api/work-plans", cancellationToken);
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .OrderBy(w => w.PlanNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkPlan?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        if (UseServer)
        {
            using var response = await _server!.SendAsync<object>(HttpMethod.Get, $"api/work-plans/{id}", null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            var dto = await response.Content.ReadFromJsonAsync<WorkPlanDto>(cancellationToken);
            if (dto is null)
                return null;
            var plans = new List<WorkPlan> { Map(dto) };
            await HydrateCentersAsync(plans, cancellationToken);
            return plans[0];
        }
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans
            .Include(w => w.Operations).ThenInclude(o => o.WorkCenter)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<bool> PlanNumberExistsAsync(string planNumber, int exceptId = 0, CancellationToken cancellationToken = default)
    {
        if (UseServer)
            return (await GetRemotePlansAsync("api/work-plans", cancellationToken)).Any(plan =>
                plan.Id != exceptId && string.Equals(plan.PlanNumber, planNumber, StringComparison.OrdinalIgnoreCase));
        await using var db = await _db.CreateContextAsync();
        return await db.WorkPlans.AnyAsync(w => w.PlanNumber == planNumber && w.Id != exceptId, cancellationToken);
    }

    /// <summary>Suggests the next free plan number (e.g. "WP-1005").</summary>
    public async Task<string> SuggestPlanNumberAsync(CancellationToken cancellationToken = default)
    {
        if (UseServer)
        {
            var remote = await GetRemotePlansAsync("api/work-plans", cancellationToken);
            var max = remote.Select(plan => int.TryParse(plan.PlanNumber.Replace("WP-", ""), out var value) ? value : 0)
                .DefaultIfEmpty(1000).Max();
            return $"WP-{max + 1}";
        }
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
        if (UseServer)
            return await SaveRemoteAsync(plan, HttpMethod.Post, "api/work-plans", cancellationToken);
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
        if (UseServer)
            return await SaveRemoteAsync(plan, HttpMethod.Put, $"api/work-plans/{plan.Id}", cancellationToken);
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
        if (UseServer)
        {
            var plan = await GetAsync(id, cancellationToken);
            if (plan is null)
                return ApplicationResult<int>.NotFound();
            using var response = await _server!.SendAsync<object>(
                HttpMethod.Delete, $"api/work-plans/{id}?version={plan.Version}", null, cancellationToken);
            return response.IsSuccessStatusCode
                ? ApplicationResult<int>.Success(id)
                : await ToApplicationResultAsync(response, cancellationToken);
        }
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

    private async Task<List<WorkPlan>> GetRemotePlansAsync(string uri, CancellationToken cancellationToken)
    {
        var dtos = await _server!.GetFromJsonAsync<List<WorkPlanDto>>(uri, cancellationToken) ?? [];
        var plans = dtos.Select(Map).ToList();
        await HydrateCentersAsync(plans, cancellationToken);
        return plans;
    }

    private async Task HydrateCentersAsync(IEnumerable<WorkPlan> plans, CancellationToken cancellationToken)
    {
        var centers = await _server!.GetFromJsonAsync<List<WorkCenterDto>>("api/work-centers", cancellationToken) ?? [];
        var byId = centers.ToDictionary(center => center.Id, center => new WorkCenter
        {
            Id = center.Id,
            Code = center.Code,
            Name = center.Name,
            HourlyRate = center.HourlyRate,
            ParallelCapacity = center.ParallelCapacity,
            TimeZoneId = center.TimeZoneId,
            IsActive = center.IsActive,
            Version = center.Version
        });
        foreach (var operation in plans.SelectMany(plan => plan.Operations))
            operation.WorkCenter = byId.GetValueOrDefault(operation.WorkCenterId);
    }

    private async Task<ApplicationResult<int>> SaveRemoteAsync(
        WorkPlan plan,
        HttpMethod method,
        string uri,
        CancellationToken cancellationToken)
    {
        using var response = await _server!.SendAsync(method, uri, ToDto(plan), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return await ToApplicationResultAsync(response, cancellationToken);
        var saved = await response.Content.ReadFromJsonAsync<WorkPlanDto>(cancellationToken);
        if (saved is null)
            return ApplicationResult<int>.PersistenceFailed();
        plan.Id = saved.Id;
        plan.Version = saved.Version;
        return ApplicationResult<int>.Success(saved.Id);
    }

    private static WorkPlan Map(WorkPlanDto dto) => new()
    {
        Id = dto.Id,
        PlanNumber = dto.PlanNumber,
        PartNumber = dto.PartNumber,
        PartName = dto.PartName,
        Revision = dto.Revision,
        Status = dto.Status,
        LotSize = dto.LotSize,
        CreatedUtc = dto.CreatedUtc,
        ModifiedUtc = dto.ModifiedUtc,
        Version = dto.Version,
        Operations = dto.Operations.Select(operation => new Operation
        {
            Id = operation.Id,
            WorkPlanId = dto.Id,
            OperationNumber = operation.OperationNumber,
            Description = operation.Description,
            WorkCenterId = operation.WorkCenterId,
            SetupTimeMinutes = operation.SetupTimeMinutes,
            TimePerPieceMinutes = operation.TimePerPieceMinutes,
            SetupFamily = operation.SetupFamily,
            Remarks = operation.Remarks
        }).ToList()
    };

    private static WorkPlanDto ToDto(WorkPlan plan) => new(
        plan.Id, plan.PlanNumber, plan.PartNumber, plan.PartName, plan.Revision,
        plan.Status, plan.LotSize, plan.CreatedUtc, plan.ModifiedUtc, plan.Version,
        plan.Operations.Select(operation => new OperationDto(
            operation.Id, operation.OperationNumber, operation.Description, operation.WorkCenterId,
            operation.SetupTimeMinutes, operation.TimePerPieceMinutes, operation.SetupFamily, operation.Remarks)).ToList());

    private static async Task<ApplicationResult<int>> ToApplicationResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return ApplicationResult<int>.NotFound();
        var error = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return ApplicationResult<int>.Conflict(new ValidationIssue("WorkPlan", error?.Code ?? "conflict"));
        if (response.StatusCode == HttpStatusCode.BadRequest && error?.Errors is not null)
            return ApplicationResult<int>.Validation(error.Errors.SelectMany(pair =>
                pair.Value.Select(message => new ValidationIssue(pair.Key, message))).ToList());
        return ApplicationResult<int>.PersistenceFailed();
    }
}
