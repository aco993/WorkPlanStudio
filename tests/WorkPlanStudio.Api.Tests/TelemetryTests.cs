using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using WorkPlanStudio.Api.Telemetry;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>
/// The application with an in-memory exporter attached to the pipeline it ships
/// with. Assertions are made on what was <em>exported</em>, so the health-probe
/// filter and the tags are tested the way an operator would see them, not the
/// way the code meant them.
/// </summary>
public sealed class TelemetryFixture : ApiFactory
{
    public TelemetryFixture() : base("telemetry")
    {
    }

    public List<Activity> Spans { get; } = [];

    public List<Metric> Metrics { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services
            .ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(Spans))
            .ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(Metrics)));
    }

    /// <summary>Pushes everything recorded so far through the exporters.</summary>
    public void Flush()
    {
        Services.GetRequiredService<TracerProvider>().ForceFlush();
        Services.GetRequiredService<MeterProvider>().ForceFlush();
    }
}

/// <summary>
/// The API's own spans and instruments (ADR 0029): what a schedule run did, and
/// how often sign-in is refused, without naming anybody.
/// </summary>
[Collection(SharedSpanSource.Name)]
public sealed class TelemetryTests : IClassFixture<TelemetryFixture>
{
    private readonly TelemetryFixture _api;

    public TelemetryTests(TelemetryFixture api)
    {
        _api = api;

        // Spans are per test; the metric sums are taken before and after.
        _api.Flush();
        _api.Spans.Clear();
    }

    private static long Sum(IEnumerable<Metric> metrics, string name, string tag, string value)
    {
        long total = 0;
        foreach (var metric in metrics.Where(m => m.Name == name))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var t in point.Tags)
                {
                    if (t.Key == tag && Equals(t.Value, value))
                        total += metric.MetricType == MetricType.Histogram ? point.GetHistogramCount() : point.GetSumLong();
                }
            }
        }
        return total;
    }

    [Fact]
    public async Task A_schedule_run_is_one_span_with_the_reads_and_the_search_beneath_it()
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.PostAsJsonAsync("/api/schedule/run", new ScheduleRunRequest(), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        _api.Flush();

        var run = Assert.Single(_api.Spans, s => s.DisplayName == "schedule.run" && s.Source.Name == ApiTelemetry.Name);
        Assert.Equal("scheduled", run.GetTagItem("workplan.outcome"));
        Assert.True((int)run.GetTagItem("workplan.operations")! > 0);

        var children = _api.Spans.Where(s => s.ParentSpanId == run.SpanId).Select(s => s.DisplayName).ToList();
        Assert.Contains("schedule.load", children);
        Assert.Contains("schedule.solve", children);

        var load = _api.Spans.Single(s => s.DisplayName == "schedule.load" && s.ParentSpanId == run.SpanId);
        Assert.Equal(3, load.GetTagItem("workplan.orders"));

        // And it hangs under the HTTP request, so a trace starts at the route.
        var request = _api.Spans.Single(s => s.SpanId == run.ParentSpanId);
        Assert.Equal("Microsoft.AspNetCore", request.Source.Name);
    }

    [Fact]
    public async Task A_schedule_run_is_timed_and_counted_by_outcome()
    {
        var client = await _api.SignedInAsync("planner");

        (await client.PostAsJsonAsync("/api/schedule/run", new ScheduleRunRequest(), TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        _api.Flush();

        Assert.True(Sum(_api.Metrics, "workplan.schedule.run.duration", "outcome", "scheduled") >= 1);
        Assert.Contains(_api.Metrics, m => m.Name == "workplan.schedule.operations");
    }

    [Fact]
    public async Task A_refused_sign_in_is_counted_by_reason_and_names_nobody()
    {
        var client = _api.CreateClient();
        _api.Flush();
        var before = Sum(_api.Metrics, "workplan.auth.sign_in.rejected", "reason", "invalid_credentials");

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("supervisor", "not-the-password-at-all"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        _api.Flush();

        Assert.True(Sum(_api.Metrics, "workplan.auth.sign_in.rejected", "reason", "invalid_credentials") > before);
        foreach (var metric in _api.Metrics.Where(m => m.Name == "workplan.auth.sign_in.rejected"))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                    Assert.DoesNotContain("supervisor", tag.Value?.ToString() ?? "", StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Health_probes_are_not_traced()
    {
        var client = _api.CreateClient();

        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/plant-settings", TestContext.Current.CancellationToken)).Dispose();
        _api.Flush();

        var traced = _api.Spans
            .Where(s => s.Source.Name == "Microsoft.AspNetCore")
            .Select(s => s.GetTagItem("url.path") as string ?? "")
            .ToList();
        Assert.Contains("/api/plant-settings", traced);   // the filter does not swallow everything
        Assert.DoesNotContain(traced, path => path.StartsWith("/health", StringComparison.Ordinal));
    }
}

/// <summary>
/// The span source is process-wide, so another host running a schedule at the
/// same time would put its spans in this class's list. One at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedSpanSource
{
    public const string Name = "telemetry";
}
