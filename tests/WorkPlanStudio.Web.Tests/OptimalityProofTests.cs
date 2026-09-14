using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.Services.Scheduling;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The KPI cards invite a question they never answered: is this the best
/// schedule? The exact solver can settle it for small instances, and the answer
/// is often "no" — the heuristic searches the order jobs are dispatched in, and
/// the best dispatch order is not the best schedule.
/// <para>
/// These tests hold the wording to what was actually proved. "Optimal" may only
/// appear when the search finished; a search that ran out of budget must say so
/// rather than round itself up.
/// </para>
/// </summary>
public sealed class OptimalityProofTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();
    private FakeOptimalityProver _prover = null!;

    private void Arrange()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProductionScheduleService>(new FakeScheduleService { Result = Sample.OnTime() });
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton<RuleBasedNarrator>();
        Services.AddSingleton<IAssistantConfig>(new FakeAssistantConfig());
        Services.AddSingleton(new HttpClient());
        Services.AddSingleton<ScheduleAssistant>();
        Services.AddSingleton<OfflineScheduleAnswerer>();
        Services.AddSingleton<ScheduleChat>();
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("proof.db", new FakeStorage())));
        Services.AddScheduleExport();
        _prover = Services.AddOptimalityProver();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private IRenderedComponent<SchedulePage> Rendered()
    {
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".proof-row")));
        return cut;
    }

    private static async Task PressAsync(IRenderedComponent<SchedulePage> cut) =>
        await cut.InvokeAsync(() => cut.Find(".proof-row button").Click());

    [Fact]
    public void The_answer_region_exists_from_the_first_paint_and_is_empty()
    {
        Arrange();
        var cut = Rendered();

        var answer = cut.Find(".proof-answer");
        Assert.Equal("polite", answer.GetAttribute("aria-live"));
        Assert.Equal("status", answer.GetAttribute("role"));
        Assert.Equal("", answer.TextContent.Trim());
    }

    [Fact]
    public async Task A_proved_optimum_the_schedule_already_reached_says_so()
    {
        Arrange();
        _prover.Proof = new OptimalityProof(
            OptimalityProofStatus.ScheduleIsOptimal, 42, 42, 42, 12, 1234, TimeSpan.FromSeconds(1.26));

        var cut = Rendered();
        await PressAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Sched_Proof_Optimal", cut.Find(".proof-answer").TextContent, StringComparison.Ordinal));
        Assert.Equal(1, _prover.Calls);
    }

    /// <summary>The answer the project would rather not show, and has to.</summary>
    [Fact]
    public async Task A_better_schedule_is_reported_with_the_gap()
    {
        Arrange();
        _prover.Proof = new OptimalityProof(
            OptimalityProofStatus.BetterScheduleExists, 120, 100, 100, 14, 9876, TimeSpan.FromSeconds(2));

        var cut = Rendered();
        await PressAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Sched_Proof_Better", cut.Find(".proof-answer").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Sched_Proof_Optimal", cut.Find(".proof-answer").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_ran_out_of_budget_never_claims_optimality()
    {
        Arrange();
        _prover.Proof = new OptimalityProof(
            OptimalityProofStatus.NotProved, 120, null, 90, 30, 50_000, TimeSpan.FromSeconds(10));

        var cut = Rendered();
        await PressAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Sched_Proof_NotProved", cut.Find(".proof-answer").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_instance_too_large_says_how_large_it_is()
    {
        Arrange();   // the double's default is "too large, 120 operations"

        var cut = Rendered();
        await PressAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Sched_Proof_TooLarge", cut.Find(".proof-answer").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_is_reported_and_does_not_reach_the_error_boundary()
    {
        Arrange();
        _prover.ExceptionToThrow = new InvalidOperationException("boom");

        var cut = Rendered();
        await PressAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Sched_Proof_Failed", cut.Find(".proof-answer").TextContent, StringComparison.Ordinal));
    }

    /// <summary>The gap is computed from the two penalties, not reported by the solver.</summary>
    [Theory]
    [InlineData(100, 100, 0)]
    [InlineData(110, 100, 10)]
    [InlineData(305, 100, 205)]
    public void The_gap_is_the_distance_above_the_proved_optimum(double heuristic, double optimum, double expected)
    {
        var proof = new OptimalityProof(
            OptimalityProofStatus.BetterScheduleExists, heuristic, optimum, optimum, 10, 1, TimeSpan.Zero);

        Assert.Equal(expected, proof.GapPercent!.Value, 6);
    }

    [Fact]
    public void Without_a_proved_optimum_there_is_no_gap_to_report()
    {
        var proof = new OptimalityProof(OptimalityProofStatus.NotProved, 120, null, 90, 30, 1, TimeSpan.Zero);
        Assert.Null(proof.GapPercent);
    }

    /// <summary>
    /// A search that ran out of budget still proved a lower bound, and the
    /// distance to it caps how much could be gained. That is the number worth
    /// showing — "we could not tell you anything" would be false.
    /// </summary>
    [Fact]
    public void A_search_that_ran_out_still_reports_what_it_proved()
    {
        var proof = new OptimalityProof(OptimalityProofStatus.NotProved, 120, null, 100, 30, 1, TimeSpan.Zero);
        Assert.Equal(20, proof.BoundGapPercent!.Value, 6);
    }

    [Fact]
    public void A_bound_of_zero_is_no_bound_at_all()
    {
        var proof = new OptimalityProof(OptimalityProofStatus.NotProved, 120, null, 0, 30, 1, TimeSpan.Zero);
        Assert.Null(proof.BoundGapPercent);
    }

    [Fact]
    public async Task A_search_that_proved_no_useful_bound_says_only_that()
    {
        Arrange();
        _prover.Proof = new OptimalityProof(
            OptimalityProofStatus.NotProved, 120, null, 0, 30, 50_000, TimeSpan.FromSeconds(2));

        var cut = Rendered();
        await PressAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("Sched_Proof_NotProvedNoBound", cut.Find(".proof-answer").TextContent, StringComparison.Ordinal));
    }
}
