using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Two ways the conversation can mislead without anything throwing: an answer
/// computed against one schedule appearing under another, and an answer given
/// confidently to a question that was not understood. Both are worse than an
/// error, because both look like the product working.
/// </summary>
public class ChatConversationSafetyTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private const string ModelAnswer = """{"choices":[{"message":{"content":"model says"}}]}""";

    // ----- one question at a time -----

    [Fact]
    public async Task A_second_question_waits_for_the_first_and_the_thread_stays_ordered()
    {
        var transport = new GatedHttpMessageHandler(ModelAnswer);
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.OpenAiCompatible), transport);
        chat.Reset(Hostile.Context());

        var first = chat.AskAsync("which work center is the bottleneck?", Ct);
        await transport.Started;
        var second = chat.AskAsync("which orders are late?", Ct);

        // The second question has not touched the thread: it is queued, not interleaved.
        Assert.Single(chat.Turns);
        Assert.True(chat.IsBusy);

        transport.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(4, chat.Turns.Count);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant],
            chat.Turns.Select(t => t.Role));
        Assert.Contains("bottleneck", chat.Turns[0].Text, StringComparison.Ordinal);
        Assert.Contains("late", chat.Turns[2].Text, StringComparison.Ordinal);
        Assert.False(chat.IsBusy);

        // The strongest statement available: the provider never had two requests
        // from this conversation open at the same time.
        Assert.Equal(1, transport.MaxConcurrent);
    }

    // ----- a new run takes the conversation with it -----

    [Fact]
    public async Task A_new_run_cancels_the_question_in_flight_instead_of_answering_it_under_the_new_schedule()
    {
        var transport = new GatedHttpMessageHandler(ModelAnswer);
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.OpenAiCompatible), transport);
        chat.Reset(Hostile.Context());

        var asking = chat.AskAsync("bottleneck", Ct);
        await transport.Started;
        chat.Reset(Hostile.Context());          // the planner pressed Generate again
        transport.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Empty(chat.Turns);
    }

    [Fact]
    public async Task An_answer_that_arrives_after_a_reset_is_dropped_rather_than_relabelled()
    {
        // The transport ignores cancellation, so the stale answer really does
        // come back; it must not be appended to the new conversation.
        var transport = new GatedHttpMessageHandler(ModelAnswer) { HonourCancellation = false };
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.OpenAiCompatible), transport);
        chat.Reset(Hostile.Context());

        var asking = chat.AskAsync("bottleneck", Ct);
        await transport.Started;
        chat.Reset(Hostile.Context());
        transport.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Empty(chat.Turns);
        Assert.DoesNotContain(chat.Turns, t => t.Text == "model says");
    }

    [Fact]
    public async Task Cancelling_a_repeated_question_withdraws_the_pending_turn_and_not_its_twin()
    {
        // Two turns with the same text are equal as records. Removing "an equal
        // turn", or "the last index", takes back the question that was already
        // answered — or throws, once a reset has emptied the list.
        var transport = new CancellingHandler { CancelOnCall = 2 };
        var chat = Hostile.Facade(Hostile.Configured(AssistantProvider.OpenAiCompatible), transport);
        chat.Reset(Hostile.Context());
        await chat.AskAsync("bottleneck", Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.AskAsync("bottleneck", transport.Caller.Token));

        Assert.Equal(2, chat.Turns.Count);
        Assert.Equal(ChatRole.User, chat.Turns[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.Turns[1].Role);
        Assert.Equal("model says", chat.Turns[1].Text);
    }

    // ----- saying "I do not know" instead of answering something else -----

    [Fact]
    public void An_unknown_order_is_named_as_unknown_rather_than_answered_with_a_summary()
    {
        var answer = new OfflineScheduleAnswerer(Hostile.Localizer()).Answer(Hostile.Context(), "Is PO-9999 on time?");

        Assert.Equal(ChatIntent.UnknownReference, answer.Intent);
        Assert.StartsWith("Ai_UnknownOrder", answer.Text);
        Assert.Contains("PO-9999", answer.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Is PO-9999 late?")]
    [InlineData("Wie steht es um PO-9999?")]
    [InlineData("PO-9999 summary please")]
    public void No_phrasing_turns_an_unknown_order_into_a_general_answer(string question)
    {
        var answer = new OfflineScheduleAnswerer(Hostile.Localizer()).Answer(Hostile.Context(), question);

        Assert.Equal(ChatIntent.UnknownReference, answer.Intent);
    }

    [Fact]
    public void An_order_that_exists_is_still_answered()
    {
        var answer = new OfflineScheduleAnswerer(Hostile.Localizer()).Answer(Hostile.Context(), "How is PO-1002 doing?");

        Assert.Equal(ChatIntent.OrderStatus, answer.Intent);
    }

    [Fact]
    public void Two_orders_differing_only_in_case_are_reported_as_ambiguous()
    {
        var context = Hostile.Context();
        var doubled = context with
        {
            Schedule = context.Schedule with
            {
                Jobs = [.. context.Schedule.Jobs, context.Schedule.Jobs[1] with { JobId = 3, Reference = "po-1002" }]
            }
        };

        var answer = new OfflineScheduleAnswerer(Hostile.Localizer()).Answer(doubled, "How is PO-1002 doing?");

        Assert.Equal(ChatIntent.UnknownReference, answer.Intent);
        Assert.StartsWith("Ai_AmbiguousOrder", answer.Text);
    }

    // ----- the prefix collision -----

    [Fact]
    public void A_work_center_code_resolves_exactly_and_not_by_prefix()
    {
        var context = Hostile.Context(firstLane: "CNC-300 — Machining", secondLane: "CNC-3000 — Big machining");

        Assert.Equal("CNC-300 — Machining", context.FindWorkCenter("CNC-300")!.WorkCenterName);
        Assert.Equal("CNC-3000 — Big machining", context.FindWorkCenter("CNC-3000")!.WorkCenterName);
        Assert.Null(context.FindWorkCenter("CNC-30"));           // was CNC-300 by prefix
        Assert.Null(context.FindWorkCenter("CNC"));
    }

    [Fact]
    public void A_question_about_a_work_center_that_is_not_here_says_so_and_lists_the_ones_that_are()
    {
        var context = Hostile.Context(firstLane: "CNC-300 — Machining", secondLane: "CNC-3000 — Big machining");

        var answer = new OfflineScheduleAnswerer(Hostile.Localizer()).Answer(context, "How busy is CNC-30?");

        Assert.Equal(ChatIntent.UnknownReference, answer.Intent);
        Assert.StartsWith("Ai_UnknownWorkCenter", answer.Text);
        Assert.Contains("CNC-3000", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bottleneck_figure_belongs_to_the_machine_the_sentence_names()
    {
        // CNC-300 is closed for two hours; CNC-3000, the bottleneck, is not closed
        // at all. Under the prefix lookup the sentence named CNC-3000 and quoted
        // CNC-300's two hours.
        var context = Hostile.Context(firstLane: "CNC-300 — Machining", secondLane: "CNC-3000 — Big machining");
        var withClosedTime = context with
        {
            Schedule = context.Schedule with
            {
                Rows =
                [
                    new GanttRow("CNC-300 — Machining", context.Schedule.Rows[0].Bars,
                        [new GanttClosedSegment(0, 2 * 3600, SegmentKind.Break, "break")]),
                    new GanttRow("CNC-3000 — Big machining", context.Schedule.Rows[1].Bars, [])
                ]
            }
        };

        var text = new OfflineScheduleAnswerer(Hostile.Localizer()).Answer(withClosedTime, "which work center is the bottleneck?").Text;

        Assert.StartsWith("Chat_Bottleneck", text);
        Assert.Contains("CNC-3000", text, StringComparison.Ordinal);
        Assert.EndsWith(Format.Hours(0), text, StringComparison.Ordinal);            // the bottleneck's own closed time
        Assert.DoesNotContain(Format.Hours(120), text, StringComparison.Ordinal);    // not the other machine's two hours
    }

    [Fact]
    public void A_bottleneck_with_no_lane_on_the_chart_quotes_no_closed_time_at_all()
    {
        var context = Hostile.Context();
        var orphaned = context with
        {
            Schedule = context.Schedule with
            {
                Explanation = context.Schedule.Explanation! with
                {
                    Bottleneck = new BottleneckFinding(99, "GONE-99 — Removed", 0.9, 3)
                }
            }
        };

        var text = new OfflineScheduleAnswerer(Hostile.Localizer()).Answer(orphaned, "bottleneck").Text;

        Assert.StartsWith("Ai_BottleneckWithoutClosed", text);
        Assert.Contains("GONE-99", text, StringComparison.Ordinal);
    }

    /// <summary>Answers normally until the chosen call, then cancels the caller's token mid-request.</summary>
    private sealed class CancellingHandler : HttpMessageHandler
    {
        private int _calls;

        public CancellationTokenSource Caller { get; } = new();

        public int CancelOnCall { get; init; } = 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_calls >= CancelOnCall)
            {
                await Caller.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(ModelAnswer, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
