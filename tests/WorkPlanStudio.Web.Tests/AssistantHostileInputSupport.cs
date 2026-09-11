using System.Net;
using System.Text;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Fixtures for the tests that treat the model path as hostile: a schedule to
/// talk about, settings that reach each provider, and transports that answer the
/// way a broken proxy, a gateway error page or a stalled stream would.
/// </summary>
internal static class Hostile
{
    public static readonly DateTime Horizon = new(2026, 6, 1, 6, 0, 0);

    /// <summary>Echoes the resource key and its arguments, so an answer's phrase and its facts are both assertable.</summary>
    public sealed class KeyEchoLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, name + " " + string.Join(" ", arguments), resourceNotFound: false);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    public static KeyEchoLocalizer Localizer() => new();

    public static AssistantSettings Configured(AssistantProvider provider, string key = "sk-secret") => new()
    {
        Enabled = true,
        Provider = provider,
        Endpoint = AssistantSettings.DefaultEndpoint(provider),
        Model = "model-x",
        ApiKey = key
    };

    /// <summary>Two work centers, one late order, a Sunday and a break inside the makespan.</summary>
    public static ScheduleChatContext Context(
        string firstLane = "CNC-200 — Turning",
        string secondLane = "SAW-10 — Cut-off Saw",
        string firstPartName = "Drive shaft")
    {
        var schedule = new ScheduleResult(
            HasData: true,
            Kpis: new ScheduleKpis(MakespanSeconds: 20 * 3600, OnTimeRate: 0.5, TotalTardinessSeconds: 3600, AverageUtilization: 0.6, LateJobCount: 1, JobCount: 2),
            Rows:
            [
                new GanttRow(firstLane,
                    [new GanttBar(1, "PO-1001", 0, 1, 0, 4 * 3600, IsLate: false) { PausedSeconds = 1800 }],
                    [new GanttClosedSegment(2 * 3600, 2 * 3600 + 1800, SegmentKind.Break, "break")]),
                new GanttRow(secondLane,
                    [new GanttBar(2, "PO-1002", 1, 1, 4 * 3600, 20 * 3600, IsLate: true)],
                    [new GanttClosedSegment(10 * 3600, 12 * 3600, SegmentKind.Sunday, "sunday")]),
            ],
            Jobs:
            [
                new JobRow(1, "PO-1001", firstPartName, 0, DueSeconds: 8 * 3600, CompletionSeconds: 4 * 3600, LatenessSeconds: -4 * 3600, IsLate: false),
                new JobRow(2, "PO-1002", "Bracket", 1, DueSeconds: 19 * 3600, CompletionSeconds: 20 * 3600, LatenessSeconds: 3600, IsLate: true),
            ],
            MakespanSeconds: 20 * 3600, MinutesPerWorkingDay: 480, LocalSearchSteps: 0)
        {
            Horizon = Horizon,
            UtilizationByWorkCenter = new Dictionary<string, double> { [firstLane] = 0.4, [secondLane] = 0.8 },
            Explanation = new ScheduleExplanation(
                new ScheduleSummary(JobCount: 2, OnTimeCount: 1, MakespanSeconds: 20 * 3600, TotalTardinessSeconds: 3600, AverageUtilization: 0.6),
                new BottleneckFinding(2, secondLane, 0.8, 1),
                [new LateJobFinding(2, "PO-1002", 3600, 2 * 3600, secondLane)],
                new ScheduleRecommendation(RecommendationKind.SwitchDispatchRule, DispatchRule.EarliestDueDate, DispatchRule.ShortestProcessingTime, 3600, 0))
        };
        var holidays = GermanHolidays.Between(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 7), GermanState.NW);
        return new ScheduleChatContext(schedule, new SchedulingParameters { DispatchRule = DispatchRule.EarliestDueDate }, WorkingTimeRules.Statutory, holidays);
    }

    /// <summary>A schedule with <paramref name="orders"/> orders, for the prompt-size bound.</summary>
    public static ScheduleChatContext LargeContext(int orders)
    {
        var jobs = Enumerable.Range(1, orders)
            .Select(i => new JobRow(i, $"PO-{4000 + i}", $"Part {i}", i % 8, DueSeconds: 8 * 3600, CompletionSeconds: 4 * 3600, LatenessSeconds: -4 * 3600, IsLate: false))
            .ToList();
        var bars = jobs.Select(j => new GanttBar(j.JobId, j.Reference, j.ColorIndex, 1, 0, 600, IsLate: false)).ToList();
        var schedule = new ScheduleResult(
            HasData: true,
            Kpis: new ScheduleKpis(MakespanSeconds: 600, OnTimeRate: 1, TotalTardinessSeconds: 0, AverageUtilization: 0.5, LateJobCount: 0, JobCount: orders),
            Rows: [new GanttRow("CNC-200 — Turning", bars)],
            Jobs: jobs,
            MakespanSeconds: 600, MinutesPerWorkingDay: 480, LocalSearchSteps: 0)
        {
            Horizon = Horizon
        };
        return new ScheduleChatContext(schedule, new SchedulingParameters(), WorkingTimeRules.Statutory, []);
    }

    public static ScheduleChat Facade(AssistantSettings settings, HttpMessageHandler transport, FakeScheduleService? scheduler = null) =>
        new(new OfflineScheduleAnswerer(Localizer()), new FakeAssistantConfig { Settings = settings },
            scheduler ?? new FakeScheduleService(), new HttpClient(transport), Localizer());

    /// <summary>
    /// The bodies an attacker, a broken proxy or a half-written stream produces.
    /// Every one is valid for no provider, or valid JSON of the wrong shape, or
    /// not JSON at all — and every provider has to survive all of them.
    /// </summary>
    public static string[] MalformedBodies =>
    [
        """{"choices":[null]}""",                            // the null element that used to throw
        """{"choices":[{"message":null}]}""",
        """{"choices":[{"message":{"role":"assistant"}}]}""", // no content property
        """{"choices":[]}""",
        """{"choices":null}""",
        """{"choices":"not-an-array"}""",                     // wrong JSON type
        """{"choices":[{"message":{"content":42}}]}""",       // wrong scalar type
        """{"content":[null]}""",                             // Anthropic, null block
        """{"content":[{"type":"text"}]}""",                  // Anthropic, no text
        """{"content":[{"type":"tool_use","id":"x"}]}""",     // Anthropic, no text block at all
        """{"candidates":[null]}""",                          // Gemini, null candidate
        """{"candidates":[{"content":null}]}""",
        """{"candidates":[{"content":{"parts":[null]}}]}""",
        """{"candidates":[{"content":{"parts":[]}}]}""",
        "{}",
        "null",
        "[]",
        "0",
        "\"just a string\"",
        "<!DOCTYPE html><html><body><h1>502 Bad Gateway</h1></body></html>",   // a 200 with an error page
        """{"choices":[{"message":{"content":"tru""",                          // truncated mid-token
        "",                                                                    // empty body
        "   ",                                                                 // whitespace only
        """{"choices":[{"message":{"content":null}}]}""",               // explicit JSON null
    ];

    public static IChatProvider Provider(AssistantProvider provider, HttpMessageHandler transport, TimeSpan? budget = null) =>
        ChatProviders.Create(new HttpClient(transport), Configured(provider), budget);
}

/// <summary>A transport that replies with one canned body and records what it was sent.</summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public RecordingHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body = body;
        _status = status;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string LastRequestBody { get; private set; } = "";
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// Returns response headers at once and then dribbles the body out, one chunk
/// per <see cref="Delay"/> — a stalled provider, which is the shape the 20-second
/// budget has to survive now that the body is read separately from the send.
/// </summary>
internal sealed class SlowBodyHttpMessageHandler : HttpMessageHandler
{
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Completes once the response headers have been handed back.</summary>
    public Task HeadersSent => _headersSent.Task;

    private readonly TaskCompletionSource _headersSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(Delay))
        };
        _headersSent.TrySetResult();
        return Task.FromResult(response);
    }

    private sealed class StallingStream : Stream
    {
        private readonly TimeSpan _delay;
        private bool _drained;

        public StallingStream(TimeSpan delay) => _delay = delay;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_drained)
                return 0;

            // One chunk, very late, then end of stream. A read that waits this
            // out rather than giving up is a read the budget is not covering.
            await Task.Delay(_delay, cancellationToken);
            _drained = true;
            buffer.Span[0] = (byte)'{';
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Holds every request open until the test releases it, so two questions can be
/// in flight at once and a reset can land in the middle of one.
/// </summary>
internal sealed class GatedHttpMessageHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _body;

    public GatedHttpMessageHandler(string body) => _body = body;

    /// <summary>Completes when the first request reaches the transport.</summary>
    public Task Started => _started.Task;

    /// <summary>When false, a released request answers even though its token was cancelled.</summary>
    public bool HonourCancellation { get; init; } = true;

    public int Calls { get; private set; }

    /// <summary>The most requests this transport ever had open at once.</summary>
    public int MaxConcurrent { get; private set; }

    private int _inFlight;

    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _inFlight));
        _started.TrySetResult();
        try
        {
            if (HonourCancellation)
                await _release.Task.WaitAsync(cancellationToken);
            else
                await _release.Task;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>
/// The browser settings helper, in memory: <c>workplanSettings.get</c> and
/// <c>.set</c> over a dictionary, so the storage layout — which key holds what —
/// is directly assertable.
/// </summary>
internal sealed class FakeBrowserSettings : IJSRuntime
{
    public Dictionary<string, string> Store { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        var name = args?.Length > 0 ? args[0] as string ?? "" : "";
        switch (identifier)
        {
            case "workplanSettings.get":
                return ValueTask.FromResult((TValue)(object?)Store.GetValueOrDefault(name)!);
            case "workplanSettings.set":
                Store[name] = args?.Length > 1 ? args[1] as string ?? "" : "";
                return ValueTask.FromResult(default(TValue)!);
            default:
                throw new InvalidOperationException("unexpected interop call: " + identifier);
        }
    }
}
