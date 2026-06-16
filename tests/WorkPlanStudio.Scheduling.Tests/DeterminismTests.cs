namespace WorkPlanStudio.Scheduling.Tests;

public class DeterminismTests
{
    [Fact]
    public void Same_seed_and_inputs_produce_an_identical_schedule()
    {
        var a = new SchedulingEngine().Run(SearchTests.MediumScenario(DispatchRule.EarliestDueDate, seed: 777));
        var b = new SchedulingEngine().Run(SearchTests.MediumScenario(DispatchRule.EarliestDueDate, seed: 777));
        Assert.Equal(a.Schedule.Signature(), b.Schedule.Signature());
    }

    [Fact]
    public void A_run_is_reproducible_for_its_seed()
    {
        var a = new SchedulingEngine().Run(SearchTests.MediumScenario(DispatchRule.WeightedShortestProcessingTime, seed: 9));
        var b = new SchedulingEngine().Run(SearchTests.MediumScenario(DispatchRule.WeightedShortestProcessingTime, seed: 9));
        Assert.Equal(a.Schedule.Signature(), b.Schedule.Signature());
    }

    [Fact]
    public void Schedule_is_independent_of_input_collection_order()
    {
        var forward = SearchTests.MediumScenario(DispatchRule.EarliestDueDate, seed: 42);
        var reversed = new SchedulingContext(
            forward.Jobs.Reverse().ToArray(),
            forward.Machines.Values.Reverse().ToArray(),
            forward.Parameters);

        var a = new SchedulingEngine().Run(forward);
        var b = new SchedulingEngine().Run(reversed);

        Assert.Equal(a.Schedule.Signature(), b.Schedule.Signature());
    }

    [Fact]
    public void Prng_is_stable_for_a_known_seed()
    {
        var rng = new DeterministicRandom(12345);
        var values = new ulong[5];
        for (int i = 0; i < values.Length; i++) values[i] = rng.NextUInt64();

        // Golden values pin the algorithm against accidental change.
        ulong[] expected =
        [
            2212426679966491084UL,
            1492905835087762364UL,
            14371891670373721988UL,
            7561351227061177014UL,
            2293113043446686077UL
        ];
        Assert.Equal(expected, values);
    }

    [Fact]
    public void Different_run_indices_yield_different_streams()
    {
        var first = DeterministicRandom.ForRun(100, 1).NextUInt64();
        var second = DeterministicRandom.ForRun(100, 2).NextUInt64();
        Assert.NotEqual(first, second);
    }
}
