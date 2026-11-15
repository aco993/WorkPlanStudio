using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace WorkPlanStudio.Api.Telemetry;

/// <summary>
/// OpenTelemetry for the API: traces, metrics and logs, one resource, and an
/// exporter only when somebody is listening.
/// </summary>
public static class TelemetrySetup
{
    /// <summary>The service name every signal carries, unless <c>OTEL_SERVICE_NAME</c> overrides it.</summary>
    public const string ServiceName = "workplan-studio-api";

    /// <summary>
    /// Registers the SDK and, when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, the
    /// OTLP exporter for all three signals.
    /// <para>
    /// The exporter is conditional because its default is
    /// <c>http://localhost:4317</c>: without this check every instance of the
    /// API, including the test host, would retry a collector that does not exist
    /// and log the failure. With it, the SDK still runs - spans still carry
    /// trace ids into the logs, and in-process listeners such as the tests
    /// still see every instrument - but nothing leaves the process until a
    /// deployment says where to.
    /// </para>
    /// <para>
    /// Health probes are filtered out of the traces. An orchestrator calls them
    /// every few seconds, and a trace store full of <c>GET /health</c> is a trace
    /// store nobody can read.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    public static WebApplicationBuilder AddApiTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<ApiTelemetry>();

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing => tracing
                .AddSource(ApiTelemetry.Name)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health")))
            .WithMetrics(metrics => metrics
                .AddMeter(ApiTelemetry.Name)
                .AddAspNetCoreInstrumentation())
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        return builder;
    }
}
