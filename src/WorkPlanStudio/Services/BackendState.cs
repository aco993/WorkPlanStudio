using System.Net.Http.Json;
using System.Text.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Services;

public enum BackendMode
{
    Local,
    Server,
    Unavailable
}

public sealed class BackendState(HttpClient httpClient, IConfiguration configuration)
{
    private Task<BackendMode>? _initialization;
    public BackendMode Mode { get; private set; } = BackendMode.Local;

    public Task<BackendMode> InitializeAsync(CancellationToken cancellationToken = default) =>
        _initialization ??= DetectAsync(cancellationToken);

    private async Task<BackendMode> DetectAsync(CancellationToken cancellationToken)
    {
        var configured = configuration["Backend:Mode"] ?? "Auto";
        if (configured.Equals("Local", StringComparison.OrdinalIgnoreCase))
            return Mode = BackendMode.Local;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var readiness = await httpClient.GetFromJsonAsync<ReadinessStatus>("api/health/ready", timeout.Token);
            if (readiness?.Status == "ready")
                return Mode = BackendMode.Server;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        {
            // Auto mode deliberately falls back to the self-contained portfolio demo.
        }
        return Mode = configured.Equals("Server", StringComparison.OrdinalIgnoreCase)
            ? BackendMode.Unavailable
            : BackendMode.Local;
    }
}
