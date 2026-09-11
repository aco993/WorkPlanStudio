using Microsoft.JSInterop;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The seam between the page and the engine. These tests are about <i>how</i> the
/// search is executed — that it yields, that it reports, that it stops — rather
/// than about what it produces, which the engine's own suite covers.
/// </summary>
public class ScheduleRunnerTests
{
    private static SchedulingContext Context(int restarts, int localSearchSteps = 400, int jobs = 12)
    {
        var machines = new List<MachineCapacity>
        {
            new(1, "SAW-10", 1),
            new(2, "CNC-200", 2)
        };

        var list = new List<ProductionJob>();
        for (var i = 1; i <= jobs; i++)
        {
            list.Add(new ProductionJob
            {
                Id = i,
                Reference = $"PO-{i}",
                ReleaseSeconds = 0,
                ExplicitDueSeconds = 3600L * (10 + i),
                Weight = 1 + i % 3,
                Steps =
                [
                    new JobStep(1, 1, 600 + 60 * i),
                    new JobStep(2, 2, 900 + 30 * i)
                ]
            });
        }

        return new SchedulingContext(list, machines, new SchedulingParameters
        {
            DueDateRule = DueDateRule.Explicit,
            MultiStartRuns = restarts,
            LocalSearchMaxSteps = localSearchSteps
        });
    }

    /// <summary>
    /// Counts how often a continuation is handed back to the host's scheduler. On
    /// WebAssembly that host is the browser's event loop, and every hand-back is
    /// one opportunity for the page to paint and for a click to be delivered.
    /// </summary>
    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);

            // Re-installed around the continuation, because a host with one thread
            // never loses its context between them. The default implementation
            // queues to the thread pool, where the context is null and every
            // further await would stop being counted — which reads as "it only
            // yielded once" and is an artefact of the harness, not of the runner.
            base.Post(_ =>
            {
                SetSynchronizationContext(this);
                d(state);
            }, null);
        }
    }

    [Fact]
    public async Task Slicing_the_run_does_not_change_the_schedule_it_produces()
    {
        // The whole design rests on this: a run taken a restart at a time has to be
        // the same run. If it were not, the page and the tests would be scheduling
        // something the engine's own suite never measures.
        var direct = new SchedulingEngine().Run(Context(restarts: 6));
        var sliced = await new CooperativeScheduleRunner()
            .RunAsync(Context(restarts: 6), null, TestContext.Current.CancellationToken);

        Assert.Equal(direct.Schedule.Signature(), sliced.Schedule.Signature());
        Assert.Equal(direct.Evaluation.Penalty, sliced.Evaluation.Penalty);
        Assert.Equal(direct.LocalSearchSteps, sliced.LocalSearchSteps);
    }

    [Fact]
    public async Task Progress_arrives_once_before_the_search_and_once_per_restart()
    {
        var reported = new List<ScheduleRunProgress>();
        await new CooperativeScheduleRunner().RunAsync(
            Context(restarts: 5),
            new InlineProgress<ScheduleRunProgress>(reported.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, reported.Count);                       // the opening report plus five restarts
        Assert.Equal(0, reported[0].RestartsCompleted);
        Assert.False(reported[0].HasBest);                     // nothing has been scheduled yet
        Assert.Equal([0, 1, 2, 3, 4, 5], reported.Select(p => p.RestartsCompleted));
        Assert.All(reported, p => Assert.Equal(5, p.TotalRestarts));
        Assert.Equal(100, reported[^1].Percent);

        // The best penalty only ever improves, which is what makes it safe to show.
        var best = reported.Skip(1).Select(p => p.BestPenalty).ToList();
        Assert.All(best, penalty => Assert.True(double.IsFinite(penalty)));
        Assert.Equal(best.Order().Reverse(), best);
    }

    [Fact]
    public async Task The_run_hands_the_thread_back_between_restarts()
    {
        // The honest version of "it runs off the UI thread". It does not: it runs on
        // the caller's thread and returns to the caller's scheduler between
        // descents, which is the only thing a single-threaded host can offer. Ten
        // restarts therefore cost at least ten hand-backs; a run that went straight
        // through would cost one, for the caller's own await.
        var previous = SynchronizationContext.Current;
        var counting = new CountingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(counting);
        try
        {
            var before = counting.Posts;
            await new CooperativeScheduleRunner()
                .RunAsync(Context(restarts: 10), null, TestContext.Current.CancellationToken);
            Assert.True(counting.Posts - before >= 10, $"only {counting.Posts - before} hand-backs for ten restarts");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task A_run_with_restarts_left_to_do_is_never_already_finished()
    {
        // Written as a task-state assertion rather than a timing one: if RunAsync
        // completed synchronously there would be nothing for a Cancel button to
        // cancel, and no render could happen while it worked.
        var run = new CooperativeScheduleRunner()
            .RunAsync(Context(restarts: 8), null, TestContext.Current.CancellationToken);

        Assert.False(run.IsCompleted);
        await run;
    }

    [Fact]
    public async Task Cancelling_stops_the_run_rather_than_returning_half_a_plan()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ScheduleRunProgress>(p =>
        {
            if (p.RestartsCompleted >= 2)
                cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CooperativeScheduleRunner().RunAsync(Context(restarts: 32), progress, cancellation.Token));
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_never_starts_a_descent()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var reported = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CooperativeScheduleRunner().RunAsync(
                Context(restarts: 4), new InlineProgress<ScheduleRunProgress>(_ => reported++), cancellation.Token));

        Assert.Equal(0, reported);
    }

    /// <summary>Records how the runner hands the thread back, without handing it anywhere.</summary>
    private sealed class CountingYield : IScheduleYield
    {
        public int Turns { get; private set; }

        public Task NextTurnAsync(CancellationToken cancellationToken)
        {
            Turns++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_thread_is_handed_back_through_the_primitive_the_runner_was_given()
    {
        // The primitive is injected rather than hard-coded because which one is used
        // decides whether a hidden browser tab makes progress at all: a timer there
        // is throttled to about one wake-up a second.
        var yielded = new CountingYield();

        await new CooperativeScheduleRunner(yielded)
            .RunAsync(Context(restarts: 7), null, TestContext.Current.CancellationToken);

        Assert.Equal(7, yielded.Turns);
    }

    /// <summary>An <see cref="IJSRuntime"/> whose every call fails the way a missing script does.</summary>
    private sealed class BrokenJsRuntime : IJSRuntime
    {
        public int Calls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls++;
            throw new JSException("workplanYield is not defined");
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    [Fact]
    public async Task A_browser_yield_whose_interop_is_missing_falls_back_instead_of_failing_the_run()
    {
        var js = new BrokenJsRuntime();
        var yielded = new BrowserScheduleYield(js);

        await yielded.NextTurnAsync(TestContext.Current.CancellationToken);
        await yielded.NextTurnAsync(TestContext.Current.CancellationToken);

        // Tried once, then remembered. A failing interop call per restart would cost
        // more than the yield it is standing in for.
        Assert.Equal(1, js.Calls);
    }

    [Fact]
    public async Task An_instance_with_nothing_to_schedule_reports_itself_complete()
    {
        var empty = new SchedulingContext([], [new MachineCapacity(1, "SAW-10", 1)], new SchedulingParameters());
        var reported = new List<ScheduleRunProgress>();

        var result = await new CooperativeScheduleRunner().RunAsync(
            empty, new InlineProgress<ScheduleRunProgress>(reported.Add), TestContext.Current.CancellationToken);

        Assert.Empty(result.Schedule.Operations);
        var only = Assert.Single(reported);
        Assert.Equal(0, only.TotalRestarts);
        Assert.Equal(100, only.Percent);   // a denominator of zero is done, not divided by
    }
}
