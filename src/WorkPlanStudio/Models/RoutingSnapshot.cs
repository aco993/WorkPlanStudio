using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkPlanStudio.Models;

/// <summary>One operation, frozen at release.</summary>
/// <remarks>
/// The work-center <em>name</em> is captured alongside its id so a schedule can
/// still be labelled after the work center is renamed or deactivated. The id is
/// what the scheduler matches on; the name is only for display.
/// </remarks>
public sealed record RoutingSnapshotOperation(
    int OperationNumber,
    string Description,
    int WorkCenterId,
    string WorkCenterName,
    decimal SetupTimeMinutes,
    decimal TimePerPieceMinutes);

/// <summary>
/// A work plan's routing as it stood when an order was released. Immutable by
/// construction — it is deserialized from a stored string and never written back.
/// </summary>
public sealed record RoutingSnapshot(
    string PlanNumber,
    string PartNumber,
    string PartName,
    string Revision,
    IReadOnlyList<RoutingSnapshotOperation> Operations)
{
    /// <summary>
    /// Version of the snapshot format. Bumped when the shape changes
    /// incompatibly, so an old order can be recognised rather than
    /// mis-deserialized into something plausible but wrong.
    /// </summary>
    public int FormatVersion { get; init; } = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Deterministic and compact: the blob is stored in the browser's
        // localStorage budget alongside the whole database.
        WriteIndented = false
    };

    /// <summary>Freezes a plan's routing. Operations are ordered so a replay is stable.</summary>
    public static RoutingSnapshot Capture(WorkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new RoutingSnapshot(
            plan.PlanNumber,
            plan.PartNumber,
            plan.PartName,
            plan.Revision ?? "",
            [.. plan.Operations
                .OrderBy(o => o.OperationNumber)
                .Select(o => new RoutingSnapshotOperation(
                    o.OperationNumber,
                    o.Description,
                    o.WorkCenterId,
                    o.WorkCenter?.Name ?? "",
                    o.SetupTimeMinutes,
                    o.TimePerPieceMinutes))]);
    }

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a stored snapshot, or <c>null</c> when it is absent or unreadable.</summary>
    public static RoutingSnapshot? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<RoutingSnapshot>(json, Options);
            return snapshot?.FormatVersion == 1 ? snapshot : null;
        }
        catch (JsonException)
        {
            // A corrupt snapshot must not take down the schedule page; the order
            // is reported as unschedulable instead.
            return null;
        }
    }
}
