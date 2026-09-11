using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The model path, treated as hostile. Everything a provider returns is a third
/// party's bytes: it can be null where the contract says it cannot, the wrong
/// JSON type, an HTML error page behind a 200, half a body, or more bytes than
/// the tab can hold. The documented promise — "on any provider failure the
/// on-device answer is shown with a note, so the chat always answers" — is only
/// true if none of that escapes as an unhandled exception.
/// </summary>
public class HostileModelResponseTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public static TheoryData<AssistantProvider, string> EveryProviderAgainstEveryBody()
    {
        var data = new TheoryData<AssistantProvider, string>();
        foreach (var provider in Enum.GetValues<AssistantProvider>())
        {
            foreach (var body in Hostile.MalformedBodies)
                data.Add(provider, body);
        }

        return data;
    }

    // ----- at the provider: an honest failure, never a null dereference -----

    [Theory]
    [MemberData(nameof(EveryProviderAgainstEveryBody))]
    public async Task A_malformed_response_fails_as_a_typed_error(AssistantProvider providerKind, string body)
    {
        var provider = Hostile.Provider(providerKind, new RecordingHttpMessageHandler(body));

        var failure = await Record.ExceptionAsync(() =>
            provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct));

        Assert.NotNull(failure);
        Assert.True(
            failure is InvalidOperationException or JsonException,
            $"{providerKind} answered {failure.GetType().Name} for {body}; the fallback only catches honest failures.");
    }

    // ----- at the page: the planner still gets an answer -----

    [Theory]
    [MemberData(nameof(EveryProviderAgainstEveryBody))]
    public async Task A_malformed_response_still_answers_the_planner(AssistantProvider providerKind, string body)
    {
        var chat = Hostile.Facade(Hostile.Configured(providerKind), new RecordingHttpMessageHandler(body));
        chat.Reset(Hostile.Context());

        var turn = await chat.AskAsync("which work center is the bottleneck?", Ct);

        Assert.Equal(NarrationSource.RuleBased, turn.Source);
        Assert.StartsWith("Chat_Bottleneck", turn.Text);          // the on-device answer, not an error
        Assert.NotNull(turn.Note);                                 // and it says the model was not used
        Assert.Equal(2, chat.Turns.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_error_status_answers_on_device_with_a_note(HttpStatusCode status)
    {
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.OpenAiCompatible),
            new RecordingHttpMessageHandler("""{"error":{"message":"sk-secret is invalid"}}""", status));
        chat.Reset(Hostile.Context());

        var turn = await chat.AskAsync("bottleneck", Ct);

        Assert.Equal(NarrationSource.RuleBased, turn.Source);
        Assert.NotNull(turn.Note);
        Assert.DoesNotContain("sk-secret", turn.Note);            // no raw provider text reaches the page
    }

    [Fact]
    public async Task A_transport_failure_that_is_not_in_any_filter_still_falls_back()
    {
        // The point of the total catch: an exception type nobody enumerated.
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.Anthropic), new ThrowingHandler());
        chat.Reset(Hostile.Context());

        var turn = await chat.AskAsync("bottleneck", Ct);

        Assert.Equal(NarrationSource.RuleBased, turn.Source);
        Assert.NotNull(turn.Note);
    }

    // ----- size -----

    [Fact]
    public async Task A_response_larger_than_the_limit_is_refused_rather_than_buffered()
    {
        var huge = BodyWithContent(new string('a', ChatProviders.MaxResponseBytes + 1024));
        var provider = Hostile.Provider(AssistantProvider.OpenAiCompatible, new RecordingHttpMessageHandler(huge));

        var failure = await Record.ExceptionAsync(() =>
            provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct));

        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task A_response_just_inside_the_limit_is_still_answered()
    {
        // The cap must not be so eager that a long but legitimate answer is lost.
        var answer = new string('a', 100_000);
        var provider = Hostile.Provider(AssistantProvider.OpenAiCompatible, new RecordingHttpMessageHandler(BodyWithContent(answer)));

        Assert.Equal(answer, await provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct));
    }

    [Fact]
    public async Task An_oversized_response_still_answers_the_planner()
    {
        var huge = BodyWithContent(new string('a', ChatProviders.MaxResponseBytes + 1024));
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.OpenAiCompatible), new RecordingHttpMessageHandler(huge));
        chat.Reset(Hostile.Context());

        var turn = await chat.AskAsync("bottleneck", Ct);

        Assert.Equal(NarrationSource.RuleBased, turn.Source);
        Assert.NotNull(turn.Note);
    }

    // ----- the budget covers the body, not only the send -----

    [Fact]
    public async Task A_body_that_never_finishes_arriving_hits_the_budget()
    {
        var transport = new SlowBodyHttpMessageHandler { Delay = TimeSpan.FromSeconds(3) };
        var provider = Hostile.Provider(AssistantProvider.Gemini, transport, TimeSpan.FromMilliseconds(250));
        var clock = Stopwatch.StartNew();

        var failure = await Record.ExceptionAsync(() =>
            provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct));

        clock.Stop();
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.True(transport.HeadersSent.IsCompletedSuccessfully, "the send finished; only the body read stalled");
        Assert.False(Ct.IsCancellationRequested, "the caller did not cancel — the budget did");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2),
            $"the budget did not govern the body read: the call took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_provider_that_runs_out_of_budget_answers_on_device_with_a_timeout_note()
    {
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.Gemini), new TimingOutHandler());
        chat.Reset(Hostile.Context());

        var turn = await chat.AskAsync("bottleneck", Ct);

        Assert.Equal(NarrationSource.RuleBased, turn.Source);
        Assert.StartsWith("Chat_Bottleneck", turn.Text);
        Assert.Contains("Sched_Ai_FailureTimeout", turn.Note);
        Assert.False(Ct.IsCancellationRequested);
    }

    // ----- what goes out -----

    [Fact]
    public async Task Every_provider_sends_an_output_budget()
    {
        foreach (var providerKind in Enum.GetValues<AssistantProvider>())
        {
            var transport = new RecordingHttpMessageHandler(ValidFor(providerKind));
            var provider = Hostile.Provider(providerKind, transport);

            await provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct);

            using var body = JsonDocument.Parse(transport.LastRequestBody);
            var budget = providerKind switch
            {
                AssistantProvider.Gemini => body.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens"),
                _ => body.RootElement.GetProperty("max_tokens")
            };
            Assert.Equal(ChatProviders.MaxOutputTokens, budget.GetInt32());
        }
    }

    [Theory]
    [InlineData("gemini-2.5-flash?alt=sse")]
    [InlineData("../../../v1/models/other")]
    [InlineData("gemini#fragment")]
    [InlineData("gemini%2e%2e")]
    public async Task A_hostile_gemini_model_cannot_rewrite_the_request_target(string model)
    {
        var transport = new RecordingHttpMessageHandler("""{"candidates":[{"content":{"parts":[{"text":"ok"}]}}]}""");
        var provider = new GeminiChatProvider(new HttpClient(transport),
            new Uri("https://generativelanguage.googleapis.com/v1beta"), model, "sk-secret");

        await provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct);

        var uri = transport.LastRequest!.RequestUri!;
        Assert.Equal("", uri.Query);
        Assert.Equal("", uri.Fragment);
        Assert.StartsWith("/v1beta/models/", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith(":generateContent", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain("/../", uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gemini-2.5-flash?alt=sse")]
    [InlineData("../../other")]
    [InlineData("model with spaces")]
    [InlineData("model\nInjected: header")]
    [InlineData("")]
    public void A_model_name_outside_the_accepted_shape_is_not_configured(string model)
    {
        var settings = Hostile.Configured(AssistantProvider.Gemini) with { Model = model };

        Assert.False(settings.IsConfigured);
    }

    [Theory]
    [InlineData("sk-live-1234567890")]
    [InlineData("AIzaSy-abcdefg_123")]
    public void A_normal_key_and_model_are_accepted(string key)
    {
        Assert.True((Hostile.Configured(AssistantProvider.Gemini, key) with { Model = "gemini-2.5-flash" }).IsConfigured);
    }

    [Fact]
    public void Settings_without_a_key_are_still_complete_apart_from_the_key()
    {
        // What the settings dialog asks, because a blank key field there means
        // "keep the stored key" and the dialog never holds the secret.
        var keyless = Hostile.Configured(AssistantProvider.Gemini, "") with { Model = "gemini-2.5-flash" };

        Assert.True(keyless.IsUsableApartFromTheKey);
        Assert.False(keyless.IsConfigured);
        Assert.False((keyless with { Model = "gemini?alt=sse" }).IsUsableApartFromTheKey);
        Assert.False((keyless with { Endpoint = "http://example.com/v1" }).IsUsableApartFromTheKey);
    }

    [Theory]
    [InlineData("sk-with\r\nInjected: header")]
    [InlineData("sk-with\ttab")]
    [InlineData("sk-with-ünicode")]
    public void A_key_that_could_split_a_header_is_not_configured(string key)
    {
        Assert.False(Hostile.Configured(AssistantProvider.Anthropic, key).IsConfigured);
    }

    /// <summary>An OpenAI-shaped body carrying <paramref name="content"/> as the answer.</summary>
    private static string BodyWithContent(string content) =>
        "{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}]}";

    private static string ValidFor(AssistantProvider provider) => provider switch
    {
        AssistantProvider.Anthropic => """{"content":[{"type":"text","text":"ok"}]}""",
        AssistantProvider.Gemini => """{"candidates":[{"content":{"parts":[{"text":"ok"}]}}]}""",
        _ => """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""
    };

    /// <summary>A transport whose own budget expired, the way HttpClient reports a timeout.</summary>
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("the request budget expired", new TimeoutException());
    }

    /// <summary>A transport that fails with something no catch filter would have listed.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new EncoderFallbackException("the transport did something nobody enumerated");
    }
}
