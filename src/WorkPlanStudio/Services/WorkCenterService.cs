using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>CRUD operations for work centers.</summary>
public sealed class WorkCenterService
{
    private readonly BrowserDatabase _db;
    private readonly BackendState? _backend;
    private readonly ServerSession? _server;

    public WorkCenterService(BrowserDatabase db) => _db = db;

    public WorkCenterService(BrowserDatabase db, BackendState backend, ServerSession server)
    {
        _db = db;
        _backend = backend;
        _server = server;
    }

    private bool UseServer => _backend?.Mode == BackendMode.Server;

    public async Task<List<WorkCenter>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (UseServer)
            return (await _server!.GetFromJsonAsync<List<WorkCenterDto>>("api/work-centers", cancellationToken) ?? [])
                .Select(Map).ToList();
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters
            .AsNoTracking()
            .OrderBy(w => w.Code)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Work centers that may be used as operation targets (active only).</summary>
    public async Task<List<WorkCenter>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        if (UseServer)
            return (await GetAllAsync(cancellationToken)).Where(center => center.IsActive).ToList();
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters
            .AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkCenter?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        if (UseServer)
        {
            using var response = await _server!.SendAsync<object>(HttpMethod.Get, $"api/work-centers/{id}", null, cancellationToken);
            return response.StatusCode == HttpStatusCode.NotFound
                ? null
                : Map((await response.Content.ReadFromJsonAsync<WorkCenterDto>(cancellationToken))!);
        }
        await using var db = await _db.CreateContextAsync();
        return await db.WorkCenters.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<bool> CodeExistsAsync(string code, int exceptId = 0, CancellationToken cancellationToken = default)
    {
        if (UseServer)
            return (await GetAllAsync(cancellationToken)).Any(center =>
                center.Id != exceptId && string.Equals(center.Code, code, StringComparison.OrdinalIgnoreCase));
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

        if (UseServer)
        {
            var dto = ToDto(center);
            var method = center.Id == 0 ? HttpMethod.Post : HttpMethod.Put;
            var uri = center.Id == 0 ? "api/work-centers" : $"api/work-centers/{center.Id}";
            using var response = await _server!.SendAsync(method, uri, dto, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return await ToApplicationResultAsync(response, cancellationToken);
            var saved = await response.Content.ReadFromJsonAsync<WorkCenterDto>(cancellationToken);
            if (saved is null)
                return ApplicationResult<int>.PersistenceFailed();
            center.Id = saved.Id;
            center.Version = saved.Version;
            return ApplicationResult<int>.Success(saved.Id);
        }

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
        if (UseServer)
            return (await GetUsageCountsAsync(cancellationToken)).GetValueOrDefault(id);
        await using var db = await _db.CreateContextAsync();
        return await db.Operations.CountAsync(o => o.WorkCenterId == id, cancellationToken);
    }

    /// <summary>All usage counts in one grouped SQL query.</summary>
    public async Task<Dictionary<int, int>> GetUsageCountsAsync(CancellationToken cancellationToken = default)
    {
        if (UseServer)
            return await _server!.GetFromJsonAsync<Dictionary<int, int>>("api/work-centers/usage-counts", cancellationToken) ?? [];
        await using var db = await _db.CreateContextAsync();
        return await db.Operations
            .GroupBy(operation => operation.WorkCenterId)
            .Select(group => new { WorkCenterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.WorkCenterId, row => row.Count, cancellationToken);
    }

    /// <summary>Deletes a work center, unless operations still reference it.</summary>
    public async Task<ApplicationResult<int>> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (UseServer)
        {
            var center = await GetAsync(id, cancellationToken);
            if (center is null)
                return ApplicationResult<int>.NotFound();
            using var response = await _server!.SendAsync<object>(
                HttpMethod.Delete, $"api/work-centers/{id}?version={center.Version}", null, cancellationToken);
            return response.IsSuccessStatusCode
                ? ApplicationResult<int>.Success(id)
                : await ToApplicationResultAsync(response, cancellationToken);
        }
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

    private static void Normalize(WorkCenter center)
    {
        center.Code = center.Code?.Trim() ?? "";
        center.Name = center.Name?.Trim() ?? "";
        center.CostCenter = center.CostCenter?.Trim() ?? "";
    }

    private static WorkCenter Map(WorkCenterDto dto) => new()
    {
        Id = dto.Id,
        Code = dto.Code,
        Name = dto.Name,
        CostCenter = dto.CostCenter,
        HourlyRate = dto.HourlyRate,
        ParallelCapacity = dto.ParallelCapacity,
        TimeZoneId = dto.TimeZoneId,
        IsActive = dto.IsActive,
        Version = dto.Version
    };

    private static WorkCenterDto ToDto(WorkCenter center) => new(
        center.Id, center.Code, center.Name, center.CostCenter, center.HourlyRate,
        center.ParallelCapacity, center.TimeZoneId, center.IsActive, center.Version);

    private static async Task<ApplicationResult<int>> ToApplicationResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return ApplicationResult<int>.NotFound();
        var error = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return ApplicationResult<int>.Conflict(new ValidationIssue("WorkCenter", error?.Code ?? "conflict"));
        if (response.StatusCode == HttpStatusCode.BadRequest && error?.Errors is not null)
            return ApplicationResult<int>.Validation(error.Errors.SelectMany(pair =>
                pair.Value.Select(message => new ValidationIssue(pair.Key, message))).ToList());
        return ApplicationResult<int>.PersistenceFailed();
    }
}
