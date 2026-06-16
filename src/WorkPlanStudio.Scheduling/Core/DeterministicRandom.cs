namespace WorkPlanStudio.Scheduling;

/// <summary>
/// A tiny, dependency-free deterministic PRNG (xorshift64*). Unlike
/// <see cref="System.Random"/> — whose algorithm has changed between .NET
/// versions and is not contractually stable — this has fixed, documented
/// constants, so a given seed yields the same stream on every platform and
/// runtime (desktop, CI and the browser's WebAssembly). That is what makes a
/// seeded schedule reproducible. It is used only to diversify multi-start
/// restarts; the dispatch itself is deterministic without any randomness.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    /// <summary>Creates a generator seeded with <paramref name="seed"/>.</summary>
    public DeterministicRandom(long seed)
    {
        // Mix the seed and guarantee a non-zero state (xorshift degenerates at 0).
        _state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL) ^ 0xD1B54A32D192ED03UL;
        if (_state == 0UL) _state = 0x9E3779B97F4A7C15UL;
    }

    /// <summary>Derives an independent stream for restart <paramref name="runIndex"/> of a base seed.</summary>
    public static DeterministicRandom ForRun(int seed, int runIndex) =>
        new(unchecked(((long)seed << 21) ^ ((long)runIndex * 0x9E3779B1L) ^ ((long)runIndex << 40)));

    /// <summary>Returns the next 64-bit value and advances the state.</summary>
    public ulong NextUInt64()
    {
        var x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return unchecked(x * 0x2545F4914F6CDD1DUL);
    }

    /// <summary>A non-negative integer in [0, <paramref name="maxExclusive"/>); 0 when the bound is ≤ 1.</summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 1) return 0;
        return (int)(NextUInt64() % (ulong)maxExclusive);
    }

    /// <summary>In-place Fisher–Yates shuffle (deterministic for a given seed).</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
