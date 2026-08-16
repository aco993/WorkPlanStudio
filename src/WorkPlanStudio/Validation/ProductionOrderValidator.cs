using WorkPlanStudio.Models;

namespace WorkPlanStudio.Validation;

/// <summary>Business rules shared by every production-order entry point.</summary>
public static class ProductionOrderValidator
{
    public const int MinQuantity = 1;
    public const int MaxQuantity = 1_000_000;
    public const int MinPriority = 1;
    public const int MaxPriority = 5;

    public static IReadOnlyList<ValidationIssue> Validate(ProductionOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var issues = new List<ValidationIssue>();

        var number = order.OrderNumber?.Trim() ?? "";
        if (number.Length == 0)
            issues.Add(new(nameof(order.OrderNumber), "Val_Required"));
        else if (number.Length > 30)
            issues.Add(new(nameof(order.OrderNumber), "Val_MaxLength", 30));

        if (order.WorkPlanId <= 0)
            issues.Add(new(nameof(order.WorkPlanId), "Val_Required"));

        if (order.Quantity < MinQuantity || order.Quantity > MaxQuantity)
            issues.Add(new(nameof(order.Quantity), "Val_QuantityRange", MinQuantity, MaxQuantity));

        if (order.Priority < MinPriority || order.Priority > MaxPriority)
            issues.Add(new(nameof(order.Priority), "Val_PriorityRange", MinPriority, MaxPriority));

        // A due date before the release is not a tight schedule, it is a typo.
        if (order.DueUtc <= order.ReleaseUtc)
            issues.Add(new(nameof(order.DueUtc), "Val_DueBeforeRelease"));

        return issues;
    }
}
