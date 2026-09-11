namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// The set of search states already expanded, so the tree stops re-deriving the
/// same partial schedule from a different direction.
/// <para>
/// The branch-and-bound extends a partial schedule one operation at a time, so
/// placing A then B and placing B then A reach the identical shop: the same
/// operations sequenced on the same slots, the same slot clocks, the same job
/// ready times. Without this memo the tree explores both, and the duplication
/// compounds with depth — it is the difference between a solver that finishes a
/// seven-job instance and one that does not.
/// </para>
/// <para>
/// States are compared <b>exactly</b>, not by hash. A hash set would be smaller
/// and faster and would, once in some very large number of nodes, prune a state
/// it had never actually seen and return a wrong answer with a confident label on
/// it. The arena below stores every state in full and compares on collision, so
/// the memo can only ever be right.
/// </para>
/// </summary>
internal sealed class SearchStateMemo
{
    private const int InitialStates = 512;

    private readonly int _stride;
    private readonly int _capacity;
    private readonly long[] _scratch;
    private long[] _arena;
    private int[] _buckets;
    private int _mask;
    private int _count;

    internal SearchStateMemo(ExactInstance instance, int capacity)
    {
        _stride = Math.Max(1, 2 * instance.JobCount + 2 * instance.SlotCount);

        // Cap by what one array can hold rather than by what was asked for: the
        // memo is an optimisation, and running out of room only costs nodes.
        _capacity = Math.Clamp(capacity, 1, 8_000_000 / _stride + 1);
        _scratch = new long[_stride];

        // Grown on demand. Most solves finish in a few hundred nodes, and a solver
        // that reserved its worst case up front would allocate megabytes to answer
        // a three-job instance.
        int initial = Math.Min(_capacity, InitialStates);
        _arena = new long[_stride * initial];
        _buckets = new int[BucketsFor(initial)];
        _mask = _buckets.Length - 1;
    }

    private static int BucketsFor(int states)
    {
        int buckets = 1;
        while (buckets < states * 2)
            buckets <<= 1;
        return buckets;
    }

    private void Grow()
    {
        int states = Math.Min(_capacity, _arena.Length / _stride * 2);
        var arena = new long[_stride * states];
        Array.Copy(_arena, arena, _count * _stride);
        _arena = arena;

        _buckets = new int[BucketsFor(states)];
        _mask = _buckets.Length - 1;
        for (int index = 0; index < _count; index++)
        {
            int bucket = (int)(Hash(_arena, index * _stride, _stride) & (uint)_mask);
            while (_buckets[bucket] != 0)
                bucket = (bucket + 1) & _mask;
            _buckets[bucket] = index + 1;
        }
    }

    /// <summary>
    /// Records <paramref name="state"/> and reports whether it is new. A state
    /// already present means this subtree has been expanded before, so the caller
    /// can cut it. Once the memo is full every state reads as new, which costs
    /// search time and cannot cost correctness.
    /// </summary>
    internal bool TryAdd(ExactSearchState state)
    {
        if (_count >= _capacity)
            return true;

        int cursor = 0;
        for (int job = 0; job < state.JobPlaced.Length; job++)
        {
            _scratch[cursor++] = state.JobPlaced[job];
            _scratch[cursor++] = state.JobReadyAt[job];
        }

        for (int slot = 0; slot < state.SlotFreeAt.Length; slot++)
        {
            _scratch[cursor++] = state.SlotFreeAt[slot];
            _scratch[cursor++] = state.SlotFamily[slot];
        }

        uint hash = Hash(_scratch, 0, _stride);
        int bucket = (int)(hash & (uint)_mask);
        while (true)
        {
            int entry = _buckets[bucket];
            if (entry == 0)
            {
                if (_count * _stride == _arena.Length)
                {
                    Grow();
                    bucket = (int)(hash & (uint)_mask);
                    while (_buckets[bucket] != 0)
                        bucket = (bucket + 1) & _mask;
                }

                Array.Copy(_scratch, 0, _arena, _count * _stride, _stride);
                _buckets[bucket] = _count + 1;
                _count++;
                return true;
            }

            if (Matches(entry - 1))
                return false;

            bucket = (bucket + 1) & _mask;
        }
    }

    private bool Matches(int index)
    {
        int offset = index * _stride;
        for (int i = 0; i < _stride; i++)
        {
            if (_arena[offset + i] != _scratch[i])
                return false;
        }

        return true;
    }

    private static uint Hash(long[] values, int offset, int length)
    {
        // FNV-1a over the 64-bit words; only used to choose a bucket, never to
        // decide equality.
        uint hash = 2166136261;
        for (int index = offset; index < offset + length; index++)
        {
            long value = values[index];
            ulong bits = (ulong)value;
            for (int shift = 0; shift < 64; shift += 8)
            {
                hash ^= (byte)(bits >> shift);
                hash *= 16777619;
            }
        }

        return hash;
    }
}
