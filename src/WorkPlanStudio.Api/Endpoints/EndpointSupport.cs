using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Security;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

internal static class EndpointSupport
{
    public static WorkCenterDto ToDto(this WorkCenter center) => new(
        center.Id, center.Code, center.Name, center.CostCenter, center.HourlyRate,
        center.ParallelCapacity, center.TimeZoneId, center.IsActive, center.Version);

    public static WorkPlanDto ToDto(this WorkPlan plan) => new(
        plan.Id, plan.PlanNumber, plan.PartNumber, plan.PartName, plan.Revision,
        plan.Status, plan.LotSize, plan.CreatedUtc, plan.ModifiedUtc, plan.Version,
        plan.Operations.OrderBy(operation => operation.OperationNumber).Select(operation => new OperationDto(
            operation.Id, operation.OperationNumber, operation.Description, operation.WorkCenterId,
            operation.SetupTimeMinutes, operation.TimePerPieceMinutes, operation.SetupFamily, operation.Remarks)).ToList());

    public static ProductionOrderDto ToDto(this ProductionOrder order) => new(
        order.Id, order.OrderNumber, order.WorkPlanId, order.WorkPlan?.PlanNumber ?? "",
        order.Quantity, order.ReleaseUtc, order.DueUtc, order.Priority, order.Status,
        order.RoutingRevision, order.CreatedUtc, order.ModifiedUtc, order.Version);

    public static ScheduleRunDto ToDto(this ScheduleRun run) => new(
        run.Id, run.Status, run.ProgressPercent, run.ResultJson, run.ErrorCode,
        run.CreatedUtc, run.StartedUtc, run.CompletedUtc, run.Version);

    public static IResult ValidationProblem(IReadOnlyList<ValidationIssue> issues) =>
        Results.BadRequest(new ApiError(
            "validation_failed",
            "One or more business rules failed.",
            issues.GroupBy(issue => issue.Field).ToDictionary(
                group => group.Key,
                group => group.Select(issue => issue.MessageKey).Distinct().ToArray())));

    public static async Task ValidateAntiforgeryAsync(IAntiforgery antiforgery, HttpContext context) =>
        await antiforgery.ValidateRequestAsync(context);

    public static async Task AuditAsync(
        ProductionDbContext db,
        ClaimsPrincipal principal,
        HttpContext context,
        string action,
        object entity,
        object? changes,
        CancellationToken cancellationToken)
    {
        AddAudit(db, principal, context, action, entity, changes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static void AddAudit(
        ProductionDbContext db,
        ClaimsPrincipal principal,
        HttpContext context,
        string action,
        object entity,
        object? changes)
    {
        var ownerId = principal.RequiredUserId();
        db.AuditEntries.Add(new AuditEntry
        {
            OwnerId = ownerId,
            ActorId = ownerId,
            Action = action,
            EntityType = entity.GetType().Name,
            EntityId = db.Entry(entity).Property("Id").CurrentValue?.ToString() ?? "",
            ChangesJson = changes is null ? null : JsonSerializer.Serialize(changes),
            CorrelationId = context.TraceIdentifier,
            OccurredUtc = DateTime.UtcNow
        });
    }

    public static async Task<IReadOnlyDictionary<int, WorkCenter>> OwnedCentersAsync(
        ProductionDbContext db,
        string ownerId,
        CancellationToken cancellationToken) =>
        await db.WorkCenters.AsNoTracking().Where(center => center.OwnerId == ownerId)
            .ToDictionaryAsync(center => center.Id, cancellationToken);
}
