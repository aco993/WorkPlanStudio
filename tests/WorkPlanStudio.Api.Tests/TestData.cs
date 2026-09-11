using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Request bodies the tests send, built in one place so a contract change shows up once.</summary>
internal static class TestData
{
    /// <summary>A valid create request for a cost center.</summary>
    /// <param name="code">The natural key, unique per test.</param>
    public static CostCenterDto NewCostCenter(string code) =>
        new(0, code, $"Test cost center {code}", null, true, "");

    /// <summary>A valid create request for a work center.</summary>
    /// <param name="code">The natural key, unique per test.</param>
    /// <param name="costCenterId">The cost center it books against, or null for "not assigned".</param>
    public static WorkCenterDto NewCenter(string code, int? costCenterId = null) =>
        new(0, code, $"Test center {code}", costCenterId, null, null, 50m, 1, "one-shift", true, "");

    /// <summary>A valid create request for a work plan with one operation on <paramref name="workCenterId"/>.</summary>
    /// <param name="planNumber">The natural key, unique per test.</param>
    /// <param name="workCenterId">Where the single operation runs.</param>
    public static WorkPlanDto NewPlan(string planNumber, int workCenterId) =>
        new(
            0,
            planNumber,
            "PART-001",
            "Test part",
            "A",
            Status: 1,
            LotSize: 10,
            Operations:
            [
                new OperationDto(10, "Cut to length", workCenterId, null, 12m, 1.5m, null),
                new OperationDto(20, "Inspect", workCenterId, null, 4m, 0.5m, "sample check")
            ],
            CreatedUtc: default,
            ModifiedUtc: default,
            ConcurrencyStamp: "");

    /// <summary>A valid create request for a draft production order.</summary>
    /// <param name="orderNumber">The natural key, unique per test.</param>
    /// <param name="workPlanId">The routing the order is built from.</param>
    /// <param name="quantity">Pieces to make.</param>
    public static ProductionOrderDto NewOrder(string orderNumber, int workPlanId, int quantity = 10) =>
        new(
            0,
            orderNumber,
            workPlanId,
            null,
            quantity,

            // Unspecified: these are plant wall-clock readings, not instants.
            ReleaseLocal: new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Unspecified),
            DueLocal: new DateTime(2026, 6, 8, 6, 0, 0, DateTimeKind.Unspecified),
            Priority: 2,
            Status: 0,
            RoutingRevision: "",
            RoutingSnapshotJson: "",
            CreatedUtc: default,
            ModifiedUtc: default,
            ConcurrencyStamp: "");
}
