using System.Net;
using System.Text;
using WorkPlanStudio.Resources;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Tests the assistant layer: the deterministic rule-based narrator, the optional
/// OpenAI-compatible narrator (against a stubbed transport, never a real network)
/// and — the important bit — the façade's graceful fallback when AI is off or fails.
/// </summary>
public class ScheduleAssistantTests
{
    private const string ValidChatJson =
        """{"choices":[{"message":{"role":"assistant","content":"- The schedule is tight.\n- Consider shortest-processing-time."}}]}""";

    private static PassThroughLocalizer<SharedResource> Localizer() => new();

    // xUnit v3: thread the ambient test cancellation token through async calls.
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A representative explanation: a hot bottleneck, one shown late job and one more
    // hidden (2 late total), and a rule-switch recommendation.
    private static ScheduleExplanation SampleExplanation() => new(
        new ScheduleSummary(JobCount: 3, OnTimeCount: 1, MakespanSeconds: 7200, TotalTardinessSeconds: 5400, AverageUtilization: 0.92),
        new BottleneckFinding(7, "CNC-300 — 5-Axis Milling", 0.92, 4),
        [new LateJobFinding(9, "WP-9", 5400, 3600, "CNC-300 — 5-Axis Milling")],
        new ScheduleRecommendation(RecommendationKind.SwitchDispatchRule, DispatchRule.EarliestDueDate, DispatchRule.ShortestProcessingTime, 5400, 1800));

    private static AssistantSettings Configured() => new()
    {
        Enabled = true,
        Endpoint = "https://api.test/v1",
        Model = "gpt-test",
        ApiKey = "sk-secret"
    };

    [Theory]
    [InlineData("http://example.com/v1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?redirect=evil")]
    public void Non_https_or_ambiguous_endpoints_are_rejected(string endpoint)
    {
        var settings = Configured() with { Endpoint = endpoint };

        Assert.False(settings.IsConfigured);
        Assert.False(settings.TryGetEndpoint(out _));
    }

    [Fact]
    public void Http_loopback_is_allowed_for_local_development()
    {
        var settings = Configured() with { Endpoint = "http://localhost:1234/v1" };

        Assert.True(settings.IsConfigured);
    }

    // ----- rule-based narrator -----

    [Fact]
    public void Rule_based_narrator_emits_a_line_per_finding_with_tones()
    {
        var narrator = new RuleBasedNarrator(Localizer());

        var lines = narrator.BuildLines(SampleExplanation());
        var keys = lines.Select(l => l.Text).ToList();

        // summary + bottleneck + late job + "more late" + recommendation
        Assert.Equal(5, lines.Count);
        Assert.Contains("Sched_Ai_Summary", keys);
        Assert.Contains("Sched_Ai_Bottleneck", keys);
        Assert.Contains("Sched_Ai_LateBlocked", keys);
        Assert.Contains("Sched_Ai_MoreLate", keys);
        Assert.Contains("Sched_Ai_RecSwitch", keys);

        Assert.Equal(FindingTone.Warning, lines[0].Tone);   // late → warning summary
        Assert.Equal(FindingTone.Info, lines[^1].Tone);     // a rule-switch tip is informational
    }

    [Fact]
    public async Task Rule_based_narrator_reports_itself_as_the_source()
    {
        var result = await new RuleBasedNarrator(Localizer()).NarrateAsync(SampleExplanation(), Ct);

        Assert.Equal(NarrationSource.RuleBased, result.Source);
        Assert.Null(result.Note);
    }

    // ----- OpenAI-compatible narrator (stubbed transport) -----

    [Fact]
    public async Task Ai_narrator_sends_the_facts_and_returns_the_model_lines()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, ValidChatJson);
        var narrator = new OpenAiScheduleNarrator(new HttpClient(stub), Configured(), language: "en");

        var result = await narrator.NarrateAsync(SampleExplanation(), Ct);

        Assert.Equal(NarrationSource.Ai, result.Source);
        Assert.Equal(2, result.Lines.Count);
        Assert.Contains("api.test", result.SourceLabel);
        Assert.Contains("gpt-test", result.SourceLabel);
        // The request actually carried the key and the computed facts (bottleneck +
        // late job). Non-ASCII is JSON-escaped in the body, so assert on ASCII parts.
        Assert.True(stub.LastRequest!.Headers.Contains("Authorization"));
        Assert.Contains("CNC-300", stub.LastRequestBody);
        Assert.Contains("WP-9", stub.LastRequestBody);
    }

    [Fact]
    public async Task Ai_narrator_throws_on_an_http_error()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{}");
        var narrator = new OpenAiScheduleNarrator(new HttpClient(stub), Configured(), language: "en");

        await Assert.ThrowsAsync<HttpRequestException>(() => narrator.NarrateAsync(SampleExplanation(), Ct));
    }

    // ----- façade: selection + graceful fallback -----

    [Fact]
    public async Task Facade_uses_rule_based_when_ai_is_not_configured()
    {
        var config = new FakeAssistantConfig();   // default: disabled
        var assistant = new ScheduleAssistant(
            new RuleBasedNarrator(Localizer()), config,
            new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, ValidChatJson)), Localizer());

        var result = await assistant.ExplainWithAiAsync(SampleExplanation(), Ct);

        Assert.Equal(NarrationSource.RuleBased, result.Source);
        Assert.Null(result.Note);   // not-configured is a normal state, not a fallback
    }

    [Fact]
    public async Task Facade_falls_back_to_rule_based_when_ai_fails()
    {
        var config = new FakeAssistantConfig { Settings = Configured() };
        var assistant = new ScheduleAssistant(
            new RuleBasedNarrator(Localizer()), config,
            new HttpClient(new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{}")), Localizer());

        var result = await assistant.ExplainWithAiAsync(SampleExplanation(), Ct);

        Assert.Equal(NarrationSource.RuleBased, result.Source);
        Assert.NotNull(result.Note);   // the fallback is surfaced to the user
    }

    [Fact]
    public async Task Facade_propagates_caller_cancellation()
    {
        var config = new FakeAssistantConfig { Settings = Configured() };
        var assistant = new ScheduleAssistant(
            new RuleBasedNarrator(Localizer()), config,
            new HttpClient(new CancelingHttpMessageHandler()), Localizer());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => assistant.ExplainWithAiAsync(SampleExplanation(), cancellation.Token));
    }

    [Fact]
    public async Task Facade_uses_ai_when_configured_and_healthy()
    {
        var config = new FakeAssistantConfig { Settings = Configured() };
        var assistant = new ScheduleAssistant(
            new RuleBasedNarrator(Localizer()), config,
            new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, ValidChatJson)), Localizer());

        var result = await assistant.ExplainWithAiAsync(SampleExplanation(), Ct);

        Assert.Equal(NarrationSource.Ai, result.Source);
    }
}

/// <summary>A transport that observes and propagates caller cancellation.</summary>
internal sealed class CancelingHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromCanceled<HttpResponseMessage>(cancellationToken);
}

/// <summary>A stub transport: returns a canned response and captures the last request.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpMessageHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string LastRequestBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
    }
}
