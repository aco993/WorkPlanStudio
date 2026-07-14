using WorkPlanStudio.Models;

namespace WorkPlanStudio.Contracts;

public sealed record ApiError(string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record AuthRequest(string Email, string Password);
public sealed record UserInfo(string Id, string Email, IReadOnlyList<string> Roles);
public sealed record AntiforgeryToken(string Token);

public sealed record WorkCenterDto(
    int Id,
    string Code,
    string Name,
    string CostCenter,
    decimal HourlyRate,
    int ParallelCapacity,
    string TimeZoneId,
    bool IsActive,
    long Version);

public sealed record OperationDto(
    int Id,
    int OperationNumber,
    string Description,
    int WorkCenterId,
    decimal SetupTimeMinutes,
    decimal TimePerPieceMinutes,
    string SetupFamily,
    string? Remarks);

public sealed record WorkPlanDto(
    int Id,
    string PlanNumber,
    string PartNumber,
    string PartName,
    string? Revision,
    WorkPlanStatus Status,
    int LotSize,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    long Version,
    IReadOnlyList<OperationDto> Operations);

public sealed record ProductionOrderDto(
    int Id,
    string OrderNumber,
    int WorkPlanId,
    string WorkPlanNumber,
    int Quantity,
    DateTime ReleaseUtc,
    DateTime DueUtc,
    int Priority,
    ProductionOrderStatus Status,
    string RoutingRevision,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    long Version);

public sealed record CalendarShiftDto(int Id, int WorkCenterId, DayOfWeek DayOfWeek, int StartMinute, int EndMinute);
public sealed record MachineDowntimeDto(int Id, int WorkCenterId, DateTime StartUtc, DateTime EndUtc, string Reason);
public sealed record SetupTransitionDto(int Id, int WorkCenterId, string FromFamily, string ToFamily, int DurationMinutes);
public sealed record CapacityProfileDto(
    IReadOnlyList<CalendarShiftDto> Shifts,
    IReadOnlyList<MachineDowntimeDto> Downtimes,
    IReadOnlyList<SetupTransitionDto> SetupTransitions);

public sealed record CreateScheduleRunRequest(
    IReadOnlyList<int> ProductionOrderIds,
    int MultiStartRuns = 8,
    int LocalSearchMaxSteps = 2_000,
    int Seed = 20260713);

public sealed record ScheduleRunDto(
    Guid Id,
    ScheduleRunStatus Status,
    int ProgressPercent,
    string? ResultJson,
    string? ErrorCode,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    long Version);

public sealed record ReadinessStatus(string Status, string DatabaseProvider, bool MigrationsApplied);

public sealed record AssistantStatusDto(bool Enabled, string? Model);
public sealed record AssistantNarrationRequest(string Facts, string Language);
public sealed record AssistantNarrationDto(IReadOnlyList<string> Lines, string SourceLabel);
