using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Services.Remote;

/// <summary>
/// The authenticated half of the API, as a typed client.
/// <para>
/// Only the calls connected mode actually makes are here. The API supports the
/// full set of writes; this client reads master data and runs schedules,
/// because that is the boundary connected mode claims and a method that exists
/// but is never called is a promise nobody tested.
/// </para>
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="http">A client whose base address is the API root and whose handler attaches the bearer token.</param>
    public ApiClient(HttpClient http) => _http = http;

    /// <summary>The signed-in identity as the server sees it, or <c>null</c> when the session is not accepted.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<UserInfo?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/auth/me", cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken)
            : null;
    }

    /// <summary>Every work center.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<WorkCenterDto>> GetWorkCentersAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<WorkCenterDto>("api/work-centers", cancellationToken);

    /// <summary>Every recorded work-center absence.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<WorkCenterAbsenceDto>> GetAbsencesAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<WorkCenterAbsenceDto>("api/work-centers/absences", cancellationToken);

    /// <summary>Every work plan with its operations.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<WorkPlanDto>> GetWorkPlansAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<WorkPlanDto>("api/work-plans", cancellationToken);

    /// <summary>Every production order, snapshot included.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<ProductionOrderDto>> GetProductionOrdersAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<ProductionOrderDto>("api/production-orders", cancellationToken);

    /// <summary>The plant's working-time settings.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PlantSettingsDto?> GetPlantSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/plant-settings", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlantSettingsDto>(cancellationToken);
    }

    /// <summary>Runs the scheduling engine on the server over the server's released orders.</summary>
    /// <param name="request">The engine parameters.</param>
    /// <param name="cancellationToken">Cancels the call and, through it, the run.</param>
    /// <exception cref="HttpRequestException">The server refused the request or failed.</exception>
    public async Task<ScheduleRunResponse> RunScheduleAsync(
        ScheduleRunRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/schedule/run", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ScheduleRunResponse>(cancellationToken)
               ?? throw new HttpRequestException("The schedule response was empty.");
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string route, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(route, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken) ?? [];
    }
}
