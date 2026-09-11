using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Api.Mapping;

/// <summary>
/// Entity to contract, by hand.
/// <para>
/// Written out rather than generated or reflected so that the wire shape is
/// visible in one file and changing a column cannot quietly change an API. It is
/// also the boundary that keeps navigation properties and lazy loads from
/// escaping into a response.
/// </para>
/// </summary>
public static class ApiMapping
{
    /// <summary>Projects a work center, with the version token the caller must send back to update it.</summary>
    /// <param name="center">The entity.</param>
    /// <param name="stamp">Its current concurrency stamp.</param>
    public static WorkCenterDto ToDto(this WorkCenter center, string stamp)
    {
        ArgumentNullException.ThrowIfNull(center);
        return new WorkCenterDto(
            center.Id,
            center.Code,
            center.Name,
            center.CostCenter,
            center.HourlyRate,
            center.ParallelCapacity,
            center.ShiftPatternKey,
            center.IsActive,
            stamp);
    }

    /// <summary>Projects one routing step.</summary>
    /// <param name="operation">The entity.</param>
    public static OperationDto ToDto(this Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new OperationDto(
            operation.OperationNumber,
            operation.Description,
            operation.WorkCenterId,
            operation.WorkCenter?.Name,
            operation.SetupTimeMinutes,
            operation.TimePerPieceMinutes,
            operation.Remarks);
    }

    /// <summary>Projects a routing with its steps in operation-number order.</summary>
    /// <param name="plan">The entity, with <c>Operations</c> loaded.</param>
    /// <param name="stamp">Its current concurrency stamp.</param>
    public static WorkPlanDto ToDto(this WorkPlan plan, string stamp)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new WorkPlanDto(
            plan.Id,
            plan.PlanNumber,
            plan.PartNumber,
            plan.PartName,
            plan.Revision,
            (int)plan.Status,
            plan.LotSize,
            [.. plan.Operations.OrderBy(o => o.OperationNumber).Select(ToDto)],
            plan.CreatedUtc,
            plan.ModifiedUtc,
            stamp);
    }

    /// <summary>Projects a production order, snapshot included.</summary>
    /// <param name="order">The entity.</param>
    /// <param name="planNumber">The plan number of the routing it came from, when loaded.</param>
    /// <param name="stamp">Its current concurrency stamp.</param>
    public static ProductionOrderDto ToDto(this ProductionOrder order, string? planNumber, string stamp)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new ProductionOrderDto(
            order.Id,
            order.OrderNumber,
            order.WorkPlanId,
            planNumber ?? order.WorkPlan?.PlanNumber,
            order.Quantity,
            order.ReleaseUtc,
            order.DueUtc,
            order.Priority,
            (int)order.Status,
            order.RoutingRevision,
            order.RoutingSnapshotJson,
            order.CreatedUtc,
            order.ModifiedUtc,
            stamp);
    }

    /// <summary>Projects the plant settings.</summary>
    /// <param name="settings">The single row.</param>
    public static PlantSettingsDto ToDto(this PlantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PlantSettingsDto(
            settings.State,
            settings.IncludePartialHolidays,
            settings.AllowExtendedDay,
            settings.AllowExtendedNight,
            settings.SundayWorkAllowed,
            settings.HolidayWorkAllowed,
            settings.SundayBoundaryShiftHours,
            settings.MinimumRestHours,
            settings.ModifiedUtc);
    }

    /// <summary>Copies the editable fields of a settings request onto an entity, normalising the state code.</summary>
    /// <param name="target">The stored row to update.</param>
    /// <param name="source">The request.</param>
    public static void CopyFrom(this PlantSettings target, PlantSettingsDto source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.State = source.State?.Trim().ToUpperInvariant() ?? "";
        target.IncludePartialHolidays = source.IncludePartialHolidays;
        target.AllowExtendedDay = source.AllowExtendedDay;
        target.AllowExtendedNight = source.AllowExtendedNight;
        target.SundayWorkAllowed = source.SundayWorkAllowed;
        target.HolidayWorkAllowed = source.HolidayWorkAllowed;
        target.SundayBoundaryShiftHours = source.SundayBoundaryShiftHours;
        target.MinimumRestHours = source.MinimumRestHours;
    }

    /// <summary>Projects a work-center absence.</summary>
    /// <param name="absence">The entity.</param>
    public static WorkCenterAbsenceDto ToDto(this WorkCenterAbsence absence)
    {
        ArgumentNullException.ThrowIfNull(absence);
        return new WorkCenterAbsenceDto(
            absence.Id,
            absence.WorkCenterId,
            absence.Start,
            absence.End,
            (int)absence.Kind,
            absence.Label);
    }

    /// <summary>Builds an unsaved work center from a request, trimming the text fields the validator measures.</summary>
    /// <param name="dto">The request.</param>
    public static WorkCenter ToEntity(this WorkCenterDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var center = new WorkCenter();
        center.CopyFrom(dto);
        return center;
    }

    /// <summary>Copies the editable fields of a work-center request onto an entity.</summary>
    /// <param name="target">The stored row (or a new one).</param>
    /// <param name="source">The request.</param>
    public static void CopyFrom(this WorkCenter target, WorkCenterDto source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.Code = source.Code?.Trim() ?? "";
        target.Name = source.Name?.Trim() ?? "";
        target.CostCenter = source.CostCenter?.Trim() ?? "";
        target.HourlyRate = source.HourlyRate;
        target.ParallelCapacity = source.ParallelCapacity;
        target.ShiftPatternKey = source.ShiftPatternKey?.Trim() ?? "";
        target.IsActive = source.IsActive;
    }

    /// <summary>Copies the header of a work-plan request onto an entity. Operations are replaced separately.</summary>
    /// <param name="target">The stored row (or a new one).</param>
    /// <param name="source">The request.</param>
    public static void CopyHeaderFrom(this WorkPlan target, WorkPlanDto source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.PlanNumber = source.PlanNumber?.Trim() ?? "";
        target.PartNumber = source.PartNumber?.Trim() ?? "";
        target.PartName = source.PartName?.Trim() ?? "";
        target.Revision = string.IsNullOrWhiteSpace(source.Revision) ? null : source.Revision.Trim();
        target.Status = (WorkPlanStatus)source.Status;
        target.LotSize = source.LotSize;
    }

    /// <summary>Builds the operation rows of a work-plan request.</summary>
    /// <param name="source">The request.</param>
    public static List<Operation> ToOperations(this WorkPlanDto source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return [.. (source.Operations ?? []).Select(o => new Operation
        {
            OperationNumber = o.OperationNumber,
            Description = o.Description?.Trim() ?? "",
            WorkCenterId = o.WorkCenterId,
            SetupTimeMinutes = o.SetupTimeMinutes,
            TimePerPieceMinutes = o.TimePerPieceMinutes,
            Remarks = string.IsNullOrWhiteSpace(o.Remarks) ? null : o.Remarks.Trim()
        })];
    }

    /// <summary>Copies the editable fields of an order request onto an entity. Status and snapshot are not editable.</summary>
    /// <param name="target">The stored row (or a new one).</param>
    /// <param name="source">The request.</param>
    public static void CopyFrom(this ProductionOrder target, ProductionOrderDto source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.OrderNumber = source.OrderNumber?.Trim() ?? "";
        target.WorkPlanId = source.WorkPlanId;
        target.Quantity = source.Quantity;
        target.ReleaseUtc = source.ReleaseUtc;
        target.DueUtc = source.DueUtc;
        target.Priority = source.Priority;
    }
}
