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

    /// <summary>
    /// Whole streams, not one value each: comparing a single draw from two
    /// generators passes for any derivation at all, <c>ForRun(s, i) => s ^ i</c>
    /// included, which would give restart 1 and restart 3 streams that differ in
    /// one bit and shuffle almost identically.
    /// </summary>
    [Fact]
    public void Different_run_indices_yield_different_streams()
    {
        const int drawsPerRun = 16;
        var streams = new Dictionary<int, ulong[]>();

        for (int runIndex = 0; runIndex < 24; runIndex++)
        {
            var rng = DeterministicRandom.ForRun(100, runIndex);
            var draws = new ulong[drawsPerRun];
            for (int i = 0; i < draws.Length; i++)
                draws[i] = rng.NextUInt64();
            streams[runIndex] = draws;
        }

        // No two runs share a value at the same position, let alone a whole stream.
        for (int a = 0; a < streams.Count; a++)
        {
            for (int b = a + 1; b < streams.Count; b++)
            {
                int shared = streams[a].Where((value, i) => value == streams[b][i]).Count();
                Assert.True(shared == 0, $"runs {a} and {b} agree on {shared} of {drawsPerRun} draws");
            }
        }
    }

    /// <summary>
    /// And the shuffles they drive really do differ: a generator that varied but
    /// permuted the same way would be no more use than no generator at all.
    /// </summary>
    [Fact]
    public void Different_run_indices_shuffle_a_long_order_differently()
    {
        var permutations = new HashSet<string>();
        for (int runIndex = 1; runIndex <= 16; runIndex++)
        {
            var order = Enumerable.Range(0, 40).ToArray();
            DeterministicRandom.ForRun(20260616, runIndex).Shuffle(order);
            permutations.Add(string.Join(",", order));
        }

        Assert.Equal(16, permutations.Count);
    }
}
