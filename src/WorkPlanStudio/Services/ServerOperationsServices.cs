using System.Net;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Services;

public sealed record ServerResult<T>(T? Value, ApiError? Error = null)
{
    public bool IsSuccess => Error is null;
}

public sealed class ProductionOrderService(ServerSession session)
{
    public Task<List<ProductionOrderDto>?> GetAllAsync(CancellationToken cancellationToken = default) =>
        session.GetFromJsonAsync<List<ProductionOrderDto>>("api/production-orders", cancellationToken);

    public async Task<ServerResult<ProductionOrderDto>> SaveAsync(ProductionOrderDto order, CancellationToken cancellationToken = default)
    {
        var method = order.Id == 0 ? HttpMethod.Post : HttpMethod.Put;
        var uri = order.Id == 0 ? "api/production-orders" : $"api/production-orders/{order.Id}";
        using var response = await session.SendAsync(method, uri, order, cancellationToken);
        return await ResultAsync<ProductionOrderDto>(response, cancellationToken);
    }

    public async Task<ApiError?> DeleteAsync(int id, long version, CancellationToken cancellationToken = default)
    {
        using var response = await session.SendAsync<object>(
            HttpMethod.Delete, $"api/production-orders/{id}?version={version}", null, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ErrorAsync(response, cancellationToken);
    }

    internal static async Task<ServerResult<T>> ResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return new(await response.Content.ReadFromJsonAsync<T>(cancellationToken));
        return new(default, await ErrorAsync(response, cancellationToken));
    }

    internal static async Task<ApiError> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken)
        ?? new ApiError("request_failed", $"Server returned {(int)response.StatusCode}.");
}

public sealed class CapacityService(ServerSession session)
{
    public Task<CapacityProfileDto?> GetAsync(int workCenterId, CancellationToken cancellationToken = default) =>
        session.GetFromJsonAsync<CapacityProfileDto>($"api/capacity/work-centers/{workCenterId}", cancellationToken);

    public Task<ServerResult<CalendarShiftDto>> SaveShiftAsync(CalendarShiftDto value, CancellationToken token = default) =>
        SaveAsync<CalendarShiftDto>("api/capacity/shifts", value, token);
    public Task<ServerResult<MachineDowntimeDto>> SaveDowntimeAsync(MachineDowntimeDto value, CancellationToken token = default) =>
        SaveAsync<MachineDowntimeDto>("api/capacity/downtimes", value, token);
    public Task<ServerResult<SetupTransitionDto>> SaveSetupAsync(SetupTransitionDto value, CancellationToken token = default) =>
        SaveAsync<SetupTransitionDto>("api/capacity/setup-transitions", value, token);

    public async Task<ApiError?> DeleteAsync(string resource, int id, CancellationToken cancellationToken = default)
    {
        using var response = await session.SendAsync<object>(HttpMethod.Delete, $"api/capacity/{resource}/{id}", null, cancellationToken);
        return response.IsSuccessStatusCode ? null : await ProductionOrderService.ErrorAsync(response, cancellationToken);
    }

    private async Task<ServerResult<T>> SaveAsync<T>(string uri, T value, CancellationToken cancellationToken)
    {
        using var response = await session.SendAsync(HttpMethod.Post, uri, value, cancellationToken);
        return await ProductionOrderService.ResultAsync<T>(response, cancellationToken);
    }
}

public sealed class ScheduleRunService(ServerSession session)
{
    public Task<List<ScheduleRunDto>?> GetAllAsync(CancellationToken cancellationToken = default) =>
        session.GetFromJsonAsync<List<ScheduleRunDto>>("api/schedule-runs", cancellationToken);

    public Task<ScheduleRunDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        session.GetFromJsonAsync<ScheduleRunDto>($"api/schedule-runs/{id}", cancellationToken);

    public async Task<ServerResult<ScheduleRunDto>> CreateAsync(CreateScheduleRunRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await session.SendAsync(HttpMethod.Post, "api/schedule-runs", request, cancellationToken);
        return await ProductionOrderService.ResultAsync<ScheduleRunDto>(response, cancellationToken);
    }

    public async Task<ServerResult<ScheduleRunDto>> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await session.SendAsync<object>(HttpMethod.Post, $"api/schedule-runs/{id}/cancel", null, cancellationToken);
        return await ProductionOrderService.ResultAsync<ScheduleRunDto>(response, cancellationToken);
    }
}
