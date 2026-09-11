namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// The mutable half of the search: which operations have been sequenced, on which
/// slot, and where that leaves every job and every slot clock.
/// <para>
/// One instance is allocated per solve and mutated in place as the depth-first
/// search descends and backtracks, so a node costs no allocation at all. The
/// engine's heuristic went from 69 KB per candidate to zero for the same reason;
/// an exact solver that opened a million nodes at 69 KB each would allocate
/// 69 GB.
/// </para>
/// <para>
/// Everything a subtree depends on is here: the remaining operations
/// (<see cref="JobPlaced"/>), each slot's clock and change-over family, and each
/// job's ready time. That is also exactly what the state memo hashes, which is
/// what makes two different orders of the same placements collapse into one node.
/// </para>
/// </summary>
internal sealed class ExactSearchState
{
    internal long[] SlotFreeAt { get; }
    internal int[] SlotFamily { get; }
    internal int[] SlotOperationCount { get; }
    internal int[] SlotLastOperation { get; }

    internal long[] JobReadyAt { get; }
    internal int[] JobPlaced { get; }

    internal long[] OperationStart { get; }
    internal long[] OperationEnd { get; }
    internal long[] OperationSetup { get; }
    internal int[] OperationSlot { get; }

    internal int PlacedCount { get; set; }
    internal long PartialMakespan { get; set; }

    internal ExactSearchState(ExactInstance instance)
    {
        SlotFreeAt = new long[instance.SlotCount];
        SlotFamily = new int[instance.SlotCount];
        SlotOperationCount = new int[instance.SlotCount];
        SlotLastOperation = new int[instance.SlotCount];

        JobReadyAt = new long[instance.JobCount];
        JobPlaced = new int[instance.JobCount];

        OperationStart = new long[instance.OperationCount];
        OperationEnd = new long[instance.OperationCount];
        OperationSetup = new long[instance.OperationCount];
        OperationSlot = new int[instance.OperationCount];

        Reset(instance);
    }

    /// <summary>Empties the shop: no operation sequenced, every slot free and fresh.</summary>
    internal void Reset(ExactInstance instance)
    {
        Array.Clear(SlotFreeAt);
        SlotFamily.AsSpan().Fill(-1);
        Array.Clear(SlotOperationCount);
        SlotLastOperation.AsSpan().Fill(-1);

        for (int job = 0; job < JobReadyAt.Length; job++)
            JobReadyAt[job] = instance.Release[job];

        Array.Clear(JobPlaced);
        PlacedCount = 0;
        PartialMakespan = 0;
    }
}
