using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Scheduling;

namespace WorkPlanStudio.Components;

/// <summary>
/// "Is this the best possible schedule?" — asked of the schedule on screen and
/// answered by an exact solver that shares no code with the heuristic that made
/// it. Lifted out of the Schedule page with its whole state: the page knows only
/// which parameters produced the result and whether a run is in flight.
/// </summary>
public sealed partial class OptimalityProofCard
{
    [Inject] private IOptimalityProver Prover { get; set; } = null!;
    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = null!;
    [Inject] private ILogger<OptimalityProofCard> Logger { get; set; } = null!;

    /// <summary>The parameters of the run on screen — not the form's, which may have been edited since.</summary>
    [Parameter, EditorRequired] public SchedulingParameters Parameters { get; set; } = null!;

    /// <summary>True while a run is in flight: the schedule the proof would judge is about to be replaced.</summary>
    [Parameter] public bool Disabled { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private SchedulingParameters? _provedFor;

    /// <summary>
    /// A verdict belongs to one schedule. When a new run lands, the page hands in
    /// a new parameters object, and a verdict about the old schedule must not
    /// stay on screen beside the new one. While it lived on the page this was
    /// never cleared: the badge would keep saying "Proved optimal" about a plan
    /// that had been replaced.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_provedFor, Parameters))
        {
            _provedFor = Parameters;
            _proof = null;
            _proofError = null;
        }
    }

    private bool _proving;
    private OptimalityProof? _proof;
    private string? _proofError;

    private async Task ProveOptimalAsync()
    {
        _proving = true;
        _proof = null;
        _proofError = null;
        try
        {
            _proof = await Prover.ProveAsync(Parameters, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Navigating away, not a failure.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Proving the schedule optimal failed");
            _proofError = L["Sched_Proof_Failed"];
        }
        finally
        {
            _proving = false;
        }
    }

    /// <summary>
    /// The sentence under the KPI cards. Every branch states what was proved and
    /// what was not — "not proved" is an outcome the search reaches often enough
    /// that hiding it would make the other answers worth less.
    /// </summary>
    private string ProofText()
    {
        if (_proofError is not null)
            return _proofError;
        if (_proof is null)
            return "";

        var proof = _proof;
        return proof.Status switch
        {
            OptimalityProofStatus.NothingToProve => L["Sched_Proof_Nothing"],
            OptimalityProofStatus.TooLarge => L["Sched_Proof_TooLarge", proof.Operations],
            OptimalityProofStatus.ScheduleIsOptimal =>
                L["Sched_Proof_Optimal", Format.Number(proof.Nodes), Format.Seconds(proof.Elapsed)],
            OptimalityProofStatus.BetterScheduleExists =>
                L["Sched_Proof_Better", Format.Number((decimal)(proof.GapPercent ?? 0), 1),
                  Format.Number(proof.Nodes), Format.Seconds(proof.Elapsed)],
            // A failed proof is not one outcome. "We looked at four million
            // schedules and none beat yours" and "we found a better one but
            // cannot say how much better" are different answers, and both used
            // to come out as a percentage above a lower bound that measures the
            // bound's weakness rather than the plan.
            OptimalityProofStatus.NotProvedNoneBetterFound =>
                L["Sched_Proof_NoneBetter", Format.Number(proof.Nodes), Format.Seconds(proof.Elapsed)],
            _ when proof.FoundBetterPercent is { } better =>
                L["Sched_Proof_FoundBetter", Format.Number((decimal)better, 1),
                  Format.Number(proof.Nodes), Format.Seconds(proof.Elapsed)],
            _ => proof.BoundGapPercent is { } bound
                ? L["Sched_Proof_NotProved", Format.Seconds(proof.Elapsed), Format.Number((decimal)bound, 1)]
                : L["Sched_Proof_NotProvedNoBound", Format.Seconds(proof.Elapsed)]
        };
    }

    /// <summary>
    /// The verdict in two or three words, for the badge. The sentence below it
    /// still carries the whole answer; the badge is what a person scanning the
    /// card reads first, so it may only say what the status established.
    /// </summary>
    private string VerdictLabel(OptimalityProof proof) => proof.Status switch
    {
        OptimalityProofStatus.ScheduleIsOptimal => L["Sched_Proof_Verdict_Optimal"],
        OptimalityProofStatus.BetterScheduleExists => L["Sched_Proof_Verdict_Better"],
        OptimalityProofStatus.TooLarge => L["Sched_Proof_Verdict_TooLarge"],
        OptimalityProofStatus.NotProvedNoneBetterFound => L["Sched_Proof_Verdict_NoneBetter"],
        _ when proof.FoundBetterPercent is not null => L["Sched_Proof_Verdict_FoundBetter"],
        _ => L["Sched_Proof_Verdict_NotProved"]
    };

    private static string VerdictTone(OptimalityProof proof) => proof.Status switch
    {
        OptimalityProofStatus.ScheduleIsOptimal => "verdict-proved",
        OptimalityProofStatus.BetterScheduleExists => "verdict-better",
        _ when proof.FoundBetterPercent is not null => "verdict-better",
        _ => "verdict-open"
    };

    /// <summary>
    /// The numbers the verdict rests on, and only those: a gap is shown when an
    /// optimum was proved, a bound when only a bound was, an improvement when
    /// the search found one. A fact the status did not establish is left out
    /// rather than shown as zero.
    /// </summary>
    private IEnumerable<(string Term, string Value)> VerdictFacts(OptimalityProof proof)
    {
        if (proof.Status == OptimalityProofStatus.TooLarge)
        {
            yield return (L["Sched_Proof_Fact_Operations"], Format.Number(proof.Operations));
            yield break;
        }

        if (proof.GapPercent is { } gap)
            yield return (L["Sched_Proof_Fact_Gap"], $"{Format.Number((decimal)gap, 1)} %");
        else if (proof.FoundBetterPercent is { } better)
            yield return (L["Sched_Proof_Fact_Improvement"], $"{Format.Number((decimal)better, 1)} %");
        else if (proof.Status == OptimalityProofStatus.NotProved && proof.BoundGapPercent is { } bound)
            yield return (L["Sched_Proof_Fact_Bound"], $"≤ {Format.Number((decimal)bound, 1)} %");

        yield return (L["Sched_Proof_Fact_Nodes"], Format.Number(proof.Nodes));
        yield return (L["Sched_Proof_Fact_Time"], Format.Seconds(proof.Elapsed));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
