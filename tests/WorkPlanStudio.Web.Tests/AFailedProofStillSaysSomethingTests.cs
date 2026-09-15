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
/// A proof that runs out of budget used to have one sentence: "Not proved within
/// 2.0 s … the schedule is at most N % above the best possible." Two things were
/// wrong with it. It blamed the clock — measured on the sample plant, the lower
/// bound sits at 93.75 after 1.3 s and four million nodes, and is still 93.75
/// after thirty seconds and seventy-five million, so waiting changes nothing. And
/// it threw away the useful half: in all those nodes the exact search never found
/// a schedule better than the one on screen, which is a statement about the plan,
/// where the percentage is a statement about the bound's weakness.
/// </summary>
public sealed class AFailedProofStillSaysSomethingTests : AppBunitContext
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
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("failed-proof.db", new FakeStorage())));
        Services.AddScheduleExport();
        _prover = Services.AddOptimalityProver();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private async Task<string> AnswerAsync(OptimalityProof proof)
    {
        Arrange();
        _prover.Proof = proof;

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".proof-row")));
        await cut.InvokeAsync(() => cut.Find(".proof-row button").Click());
        cut.WaitForAssertion(() => Assert.NotEqual("", cut.Find(".proof-answer").TextContent.Trim()));
        return cut.Find(".proof-answer").TextContent;
    }

    [Fact]
    public async Task A_search_that_found_nothing_better_says_so()
    {
        var answer = await AnswerAsync(new OptimalityProof(
            OptimalityProofStatus.NotProvedNoneBetterFound, 2394.9, null, 93.75, 30,
            4_000_000, TimeSpan.FromSeconds(2), BestFound: 2394.9));

        Assert.Contains("Sched_Proof_NoneBetter", answer, StringComparison.Ordinal);

        // The percentage above a weak lower bound is what this replaces.
        Assert.DoesNotContain("Sched_Proof_NotProved", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_did_find_better_reports_the_improvement()
    {
        var answer = await AnswerAsync(new OptimalityProof(
            OptimalityProofStatus.NotProved, 200, null, 90, 30,
            50_000, TimeSpan.FromSeconds(2), BestFound: 150));

        Assert.Contains("Sched_Proof_FoundBetter", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_found_no_schedule_at_all_keeps_the_old_sentence()
    {
        var answer = await AnswerAsync(new OptimalityProof(
            OptimalityProofStatus.NotProved, 120, null, 90, 30, 50_000, TimeSpan.FromSeconds(2)));

        Assert.Contains("Sched_Proof_NotProved", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_improvement_is_measured_against_the_schedule_on_screen()
    {
        var proof = new OptimalityProof(
            OptimalityProofStatus.NotProved, 200, null, 90, 30, 1, TimeSpan.Zero, BestFound: 150);

        Assert.Equal(25.0, proof.FoundBetterPercent!.Value, 6);
    }

    [Fact]
    public void Nothing_better_found_is_not_reported_as_an_improvement_of_zero()
    {
        var proof = new OptimalityProof(
            OptimalityProofStatus.NotProvedNoneBetterFound, 200, null, 90, 30, 1, TimeSpan.Zero, BestFound: 200);

        Assert.Null(proof.FoundBetterPercent);
    }
}
