using System.Globalization;
using System.Net;
using System.Text.Json;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The schedule conversation: the on-device answerer (intents in English and
/// German, answers built from the page's own facts), the three provider clients
/// over a stubbed transport, and the façade's what-if re-run and fallback.
/// </summary>
public class ScheduleChatTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static EchoLocalizer Localizer() => new();

    /// <summary>
    /// Echoes the key followed by the arguments, so an answer can be checked for
    /// both the phrase it chose and the facts it filled in, without the .resx.
    /// </summary>
    private sealed class EchoLocalizer : Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>
    {
        public Microsoft.Extensions.Localization.LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] =>
            new(name, name + " " + string.Join(" ", arguments), resourceNotFound: false);
        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private static readonly DateTime Horizon = new(2026, 6, 1, 6, 0, 0);

    /// <summary>Two work centers, one late order, a Sunday and a break inside the makespan.</summary>
    private static ScheduleChatContext Context(DispatchRule rule = DispatchRule.EarliestDueDate)
    {
        var schedule = new ScheduleResult(
            HasData: true,
            Kpis: new ScheduleKpis(MakespanSeconds: 20 * 3600, OnTimeRate: 0.5, TotalTardinessSeconds: 3600, AverageUtilization: 0.6, LateJobCount: 1, JobCount: 2),
            Rows:
            [
                new GanttRow("CNC-200 — Turning",
                    [new GanttBar(1, "PO-1001", 0, 1, 0, 4 * 3600, IsLate: false) { PausedSeconds = 1800 }],
                    [new GanttClosedSegment(2 * 3600, 2 * 3600 + 1800, SegmentKind.Break, "break")]),
                new GanttRow("SAW-10 — Cut-off Saw",
                    [new GanttBar(2, "PO-1002", 1, 1, 4 * 3600, 20 * 3600, IsLate: true)],
                    [new GanttClosedSegment(10 * 3600, 12 * 3600, SegmentKind.Sunday, "sunday")]),
            ],
            Jobs:
            [
                new JobRow(1, "PO-1001", "Drive shaft", 0, DueSeconds: 8 * 3600, CompletionSeconds: 4 * 3600, LatenessSeconds: -4 * 3600, IsLate: false),
                new JobRow(2, "PO-1002", "Bracket", 1, DueSeconds: 19 * 3600, CompletionSeconds: 20 * 3600, LatenessSeconds: 3600, IsLate: true),
            ],
            MakespanSeconds: 20 * 3600, MinutesPerWorkingDay: 480, LocalSearchSteps: 0)
        {
            Horizon = Horizon,
            UtilizationByWorkCenter = new Dictionary<string, double> { ["CNC-200 — Turning"] = 0.4, ["SAW-10 — Cut-off Saw"] = 0.8 },
            Explanation = new ScheduleExplanation(
                new ScheduleSummary(JobCount: 2, OnTimeCount: 1, MakespanSeconds: 20 * 3600, TotalTardinessSeconds: 3600, AverageUtilization: 0.6),
                new BottleneckFinding(2, "SAW-10 — Cut-off Saw", 0.8, 1),
                [new LateJobFinding(2, "PO-1002", 3600, 2 * 3600, "SAW-10 — Cut-off Saw")],
                new ScheduleRecommendation(RecommendationKind.SwitchDispatchRule, rule, DispatchRule.ShortestProcessingTime, 3600, 0))
        };
        var holidays = GermanHolidays.Between(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 7), GermanState.NW);
        return new ScheduleChatContext(schedule, new SchedulingParameters { DispatchRule = rule }, WorkingTimeRules.Statutory, holidays);
    }

    private static AssistantSettings Configured(AssistantProvider provider) => new()
    {
        Enabled = true,
        Provider = provider,
        Endpoint = AssistantSettings.DefaultEndpoint(provider),
        Model = "model-x",
        ApiKey = "sk-secret"
    };

    // ----- on-device answerer: intents -----

    [Theory]
    [InlineData("Which work center is the bottleneck?", ChatIntent.Bottleneck)]
    [InlineData("Welcher Arbeitsplatz ist der Engpass?", ChatIntent.Bottleneck)]
    [InlineData("Which orders are late?", ChatIntent.LateOrders)]
    [InlineData("Welche Aufträge sind verspätet?", ChatIntent.LateOrders)]
    [InlineData("Why are the machines idle at times?", ChatIntent.ClosedTime)]
    [InlineData("Warum stehen die Maschinen zeitweise still?", ChatIntent.ClosedTime)]
    [InlineData("Which working-time rules apply?", ChatIntent.Compliance)]
    [InlineData("Welche Arbeitszeitregeln gelten?", ChatIntent.Compliance)]
    [InlineData("Give me a summary", ChatIntent.Summary)]
    [InlineData("Wie sieht der Plan aus?", ChatIntent.Summary)]
    [InlineData("help", ChatIntent.Help)]
    [InlineData("", ChatIntent.Help)]
    [InlineData("How is PO-1002 doing?", ChatIntent.OrderStatus)]
    [InlineData("Wie steht es um PO-1001?", ChatIntent.OrderStatus)]
    [InlineData("Tell me about CNC-200", ChatIntent.WorkCenterStatus)]
    [InlineData("What if I switch to SPT?", ChatIntent.WhatIfRule)]
    [InlineData("Was wäre mit der Regel LPT?", ChatIntent.WhatIfRule)]
    [InlineData("banana", ChatIntent.Unknown)]
    public void Recognises_intents_in_english_and_german(string question, ChatIntent expected)
    {
        var answer = new OfflineScheduleAnswerer(Localizer()).Answer(Context(), question);

        Assert.Equal(expected, answer.Intent);
    }

    [Theory]
    [InlineData("what if I use SPT", DispatchRule.ShortestProcessingTime)]
    [InlineData("try the longest processing time rule", DispatchRule.LongestProcessingTime)]
    [InlineData("was wäre mit fifo", DispatchRule.Fifo)]
    [InlineData("switch to critical ratio", DispatchRule.CriticalRatio)]
    [InlineData("compare with wspt", DispatchRule.WeightedShortestProcessingTime)]
    [InlineData("stattdessen edd", DispatchRule.EarliestDueDate)]
    public void A_what_if_names_the_rule_to_try(string question, DispatchRule expected)
    {
        var answer = new OfflineScheduleAnswerer(Localizer()).Answer(Context(), question);

        Assert.Equal(ChatIntent.WhatIfRule, answer.Intent);
        Assert.Equal(expected, answer.WhatIfRule);
    }

    [Fact]
    public void A_rule_acronym_in_a_status_question_is_not_a_what_if()
    {
        // "EDD" alone must not trigger a re-run; the question is about lateness.
        var answer = new OfflineScheduleAnswerer(Localizer()).Answer(Context(), "which orders are late under EDD?");

        Assert.Equal(ChatIntent.LateOrders, answer.Intent);
    }

    // ----- on-device answerer: answers carry the schedule's facts -----

    [Fact]
    public void The_bottleneck_answer_names_the_center_and_its_utilisation()
    {
        var text = new OfflineScheduleAnswerer(Localizer()).Answer(Context(), "bottleneck").Text;

        Assert.StartsWith("Chat_Bottleneck", text);
        Assert.Contains("SAW-10", text);
        Assert.Contains("80 %", text);
    }

    [Fact]
    public void The_late_answer_lists_each_late_order_with_its_reason_and_the_recommendation()
    {
        var text = new OfflineScheduleAnswerer(Localizer()).Answer(Context(), "late").Text;

        Assert.Contains("Chat_Late", text);
        Assert.Contains("PO-1002", text);
        Assert.Contains("Chat_LateReasonQueue", text);
        Assert.Contains("Chat_LateRecommend", text);
        Assert.DoesNotContain("PO-1001", text);
    }

    [Fact]
    public void The_order_answer_lists_its_steps_with_real_dates_and_pauses()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var text = new OfflineScheduleAnswerer(Localizer()).Answer(Context(), "po-1001").Text;

            Assert.StartsWith("Chat_Order", text);
            Assert.Contains("Chat_OrderOnTime", text);
            Assert.Contains("CNC-200", text);
            Assert.Contains("Mon 1.6. 06:00", text);       // horizon-anchored, not "0.0 h"
            Assert.Contains("Sched_Paused", text);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void The_closed_time_answer_groups_by_reason_and_names_the_holiday()
    {
        var text = new OfflineScheduleAnswerer(Localizer()).Answer(Context(), "why idle").Text;

        Assert.Contains("Segment_Sunday", text);
        Assert.Contains("Segment_Break", text);
        Assert.Contains("Holiday_CorpusChristi", text);   // Fronleichnam 2026-06-04 in NW
    }

    [Fact]
    public void The_compliance_answer_reflects_the_plant_rules()
    {
        var context = Context() with { Rules = WorkingTimeRules.Statutory with { SundayWorkAllowed = true } };
        var text = new OfflineScheduleAnswerer(Localizer()).Answer(context, "arbzg").Text;

        Assert.StartsWith("Chat_Compliance", text);
        Assert.Contains("Chat_Permitted", text);
        Assert.Contains("Chat_Forbidden", text);   // holiday work stays forbidden
        Assert.Contains("NW", text);
    }

    [Fact]
    public void A_what_if_comparison_says_whether_the_alternative_is_better()
    {
        var answerer = new OfflineScheduleAnswerer(Localizer());
        var current = Context();
        var better = current.Schedule with { Kpis = current.Schedule.Kpis with { LateJobCount = 0, TotalTardinessSeconds = 0 } };
        var same = current.Schedule;

        Assert.Contains("Chat_WhatIfBetter", answerer.DescribeWhatIf(current, DispatchRule.ShortestProcessingTime, better));
        Assert.Contains("Chat_WhatIfSameResult", answerer.DescribeWhatIf(current, DispatchRule.ShortestProcessingTime, same));
        Assert.StartsWith("Chat_WhatIfSame", answerer.DescribeWhatIf(current, DispatchRule.EarliestDueDate, same));
    }

    [Fact]
    public void The_same_question_on_the_same_schedule_always_gets_the_same_answer()
    {
        var answerer = new OfflineScheduleAnswerer(Localizer());

        var first = answerer.Answer(Context(), "Which orders are late, and why?").Text;
        var second = answerer.Answer(Context(), "Which orders are late, and why?").Text;

        Assert.Equal(first, second);
    }

    // ----- the facts a model is shown -----

    [Fact]
    public void The_model_facts_carry_orders_centers_rules_and_holidays_in_invariant_culture()
    {
        var facts = ChatFacts.Describe(Context());

        Assert.Contains("PO-1002", facts);
        Assert.Contains("SAW-10", facts);
        Assert.Contains("Corpus Christi", facts);
        Assert.Contains("EarliestDueDate", facts);
        Assert.DoesNotContain(",0 h", facts);   // never a German decimal comma
    }

    // ----- providers over a stubbed transport -----

    [Fact]
    public async Task The_openai_provider_posts_chat_completions_with_a_bearer_token()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":" hello "}}]}""");
        var provider = ChatProviders.Create(new HttpClient(stub), Configured(AssistantProvider.OpenAiCompatible));

        var answer = await provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct);

        Assert.Equal("hello", answer);
        Assert.EndsWith("/v1/chat/completions", stub.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer sk-secret", stub.LastRequest.Headers.GetValues("Authorization").Single());
        Assert.Contains("api.openai.com", provider.Label);
        Assert.Contains("model-x", provider.Label);
    }

    [Fact]
    public async Task The_anthropic_provider_sends_the_messages_api_headers_and_folds_a_leading_narration_into_system()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, """{"content":[{"type":"text","text":"first"},{"type":"text","text":"second"}],"stop_reason":"end_turn"}""");
        var provider = ChatProviders.Create(new HttpClient(stub), Configured(AssistantProvider.Anthropic));
        var history = new List<ChatTurn>
        {
            new(ChatRole.Assistant, "narration"),
            new(ChatRole.User, "q1"),
            new(ChatRole.User, "q2"),
        };

        var answer = await provider.CompleteAsync("sys", history, Ct);

        Assert.Equal("first\nsecond", answer);
        Assert.EndsWith("/v1/messages", stub.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("sk-secret", stub.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicChatProvider.ApiVersion, stub.LastRequest.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("true", stub.LastRequest.Headers.GetValues("anthropic-dangerous-direct-browser-access").Single());

        using var body = JsonDocument.Parse(stub.LastRequestBody);
        var root = body.RootElement;
        Assert.Equal("model-x", root.GetProperty("model").GetString());
        Assert.Contains("narration", root.GetProperty("system").GetString());
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(messages);                                   // two user turns merged, narration folded
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Contains("q2", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_gemini_provider_targets_generate_content_with_the_key_in_a_header()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]}}]}""");
        var provider = ChatProviders.Create(new HttpClient(stub), Configured(AssistantProvider.Gemini));

        var answer = await provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct);

        Assert.Equal("ok", answer);
        Assert.EndsWith("/v1beta/models/model-x:generateContent", stub.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("sk-secret", stub.LastRequest.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("sk-secret", stub.LastRequest.RequestUri.ToString());   // never in the query string
        using var body = JsonDocument.Parse(stub.LastRequestBody);
        Assert.Equal("sys", body.RootElement.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_empty_model_answer_is_an_error_not_a_blank_bubble()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"   "}}]}""");
        var provider = ChatProviders.Create(new HttpClient(stub), Configured(AssistantProvider.OpenAiCompatible));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct));
    }

    [Fact]
    public void Unusable_settings_cannot_build_a_provider()
    {
        Assert.Throws<ArgumentException>(() => ChatProviders.Create(new HttpClient(), AssistantSettings.Default));
    }

    // ----- façade -----

    private static ScheduleChat Facade(AssistantSettings settings, HttpMessageHandler transport, FakeScheduleService? scheduler = null) =>
        new(new OfflineScheduleAnswerer(Localizer()), new FakeAssistantConfig { Settings = settings }, scheduler ?? new FakeScheduleService(),
            new HttpClient(transport), Localizer());

    [Fact]
    public async Task Without_a_key_the_chat_answers_on_device_and_keeps_both_turns()
    {
        var chat = Facade(AssistantSettings.Default, new StubHttpMessageHandler(HttpStatusCode.OK, "{}"));
        chat.Reset(Context());

        var turn = await chat.AskAsync("bottleneck", Ct);

        Assert.Equal(NarrationSource.RuleBased, turn.Source);
        Assert.Equal("Chat_SourceOffline", turn.SourceLabel);
        Assert.Null(turn.Note);
        Assert.Equal(2, chat.Turns.Count);
        Assert.Equal(ChatRole.User, chat.Turns[0].Role);
    }

    [Fact]
    public async Task Without_a_schedule_the_chat_says_so()
    {
        var chat = Facade(AssistantSettings.Default, new StubHttpMessageHandler(HttpStatusCode.OK, "{}"));

        var turn = await chat.AskAsync("bottleneck", Ct);

        Assert.Equal("Chat_NoData", turn.Text);
    }

    [Fact]
    public async Task A_what_if_re_runs_the_scheduler_with_the_alternative_rule()
    {
        var scheduler = new FakeScheduleService { Result = Context().Schedule };
        var chat = Facade(AssistantSettings.Default, new StubHttpMessageHandler(HttpStatusCode.OK, "{}"), scheduler);
        chat.Reset(Context());

        var turn = await chat.AskAsync("what if I switch to SPT?", Ct);

        Assert.Equal(1, scheduler.Calls);
        Assert.Equal(DispatchRule.ShortestProcessingTime, scheduler.LastParameters!.DispatchRule);
        Assert.StartsWith("Chat_WhatIf", turn.Text);
    }

    [Fact]
    public async Task With_a_key_the_model_answers_and_is_shown_the_on_device_facts()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"model says"}}]}""");
        var chat = Facade(Configured(AssistantProvider.OpenAiCompatible), stub);
        chat.Reset(Context());

        var turn = await chat.AskAsync("which orders are late?", Ct);

        Assert.Equal(NarrationSource.Ai, turn.Source);
        Assert.Equal("model says", turn.Text);
        Assert.Contains("PO-1002", stub.LastRequestBody);
        Assert.Contains("Chat_Late", stub.LastRequestBody);   // the on-device answer rides along as facts
    }

    [Fact]
    public async Task When_the_model_fails_the_on_device_answer_is_shown_with_a_note()
    {
        var chat = Facade(Configured(AssistantProvider.Anthropic), new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "{}"));
        chat.Reset(Context());

        var turn = await chat.AskAsync("bottleneck", Ct);

        Assert.Equal(NarrationSource.RuleBased, turn.Source);
        Assert.StartsWith("Chat_Bottleneck", turn.Text);
        Assert.NotNull(turn.Note);
        Assert.Equal(2, chat.Turns.Count);
    }

    [Fact]
    public async Task Reset_clears_the_conversation()
    {
        var chat = Facade(AssistantSettings.Default, new StubHttpMessageHandler(HttpStatusCode.OK, "{}"));
        chat.Reset(Context());
        await chat.AskAsync("bottleneck", Ct);

        chat.Reset(Context());

        Assert.Empty(chat.Turns);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_leaves_no_dangling_question()
    {
        var chat = Facade(Configured(AssistantProvider.OpenAiCompatible), new CancelingHttpMessageHandler());
        chat.Reset(Context());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.AskAsync("bottleneck", cancellation.Token));

        Assert.Empty(chat.Turns);
    }
}
