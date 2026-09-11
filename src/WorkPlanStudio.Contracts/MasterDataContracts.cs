namespace WorkPlanStudio.Contracts;

/// <summary>
/// A work center as the API returns it.
/// <para>
/// Deliberately not the EF entity: the entity carries navigation collections and
/// a persistence identity that mean nothing to a caller, and a DTO that is free
/// to change independently is what keeps a storage change from becoming a
/// breaking wire change. Mapping is explicit, in <c>ApiMapping</c>.
/// </para>
/// </summary>
/// <param name="Id">Server-assigned key. Zero on a create request.</param>
/// <param name="Code">Short identifier, unique across the tenant (case-insensitive).</param>
/// <param name="Name">Display name.</param>
/// <param name="CostCenter">Accounting cost center, possibly empty.</param>
/// <param name="HourlyRate">Machine-hour rate used to estimate operation cost.</param>
/// <param name="ParallelCapacity">How many jobs this center runs at once.</param>
/// <param name="ShiftPatternKey">Key of the shift-pattern preset this center is staffed by.</param>
/// <param name="IsActive">Inactive centers may not be used by released routings.</param>
/// <param name="ConcurrencyStamp">
/// Opaque version token. Send back the value you read; a mismatch means somebody
/// else changed the row first and the write is refused with 409.
/// </param>
public sealed record WorkCenterDto(
    int Id,
    string Code,
    string Name,
    string CostCenter,
    decimal HourlyRate,
    int ParallelCapacity,
    string ShiftPatternKey,
    bool IsActive,
    string ConcurrencyStamp);

/// <summary>One operation of a work plan.</summary>
/// <param name="OperationNumber">Sequence number within the plan (10, 20, 30 …).</param>
/// <param name="Description">What is done at this step.</param>
/// <param name="WorkCenterId">Where it is done.</param>
/// <param name="WorkCenterName">Display name of that work center; ignored on write.</param>
/// <param name="SetupTimeMinutes">One-off change-over time.</param>
/// <param name="TimePerPieceMinutes">Run time per piece.</param>
/// <param name="Remarks">Free text, optional.</param>
public sealed record OperationDto(
    int OperationNumber,
    string Description,
    int WorkCenterId,
    string? WorkCenterName,
    decimal SetupTimeMinutes,
    decimal TimePerPieceMinutes,
    string? Remarks);

/// <summary>A work plan (routing) with its operations.</summary>
/// <param name="Id">Server-assigned key. Zero on a create request.</param>
/// <param name="PlanNumber">Unique plan identifier.</param>
/// <param name="PartNumber">Part number this routing produces.</param>
/// <param name="PartName">Human-readable part name.</param>
/// <param name="Revision">Routing revision, optional.</param>
/// <param name="Status">0 draft, 1 released, 2 archived.</param>
/// <param name="LotSize">Default batch quantity used for costing.</param>
/// <param name="Operations">The steps, in operation-number order.</param>
/// <param name="CreatedUtc">When the plan was created.</param>
/// <param name="ModifiedUtc">When it last changed.</param>
/// <param name="ConcurrencyStamp">Opaque version token; see <see cref="WorkCenterDto.ConcurrencyStamp"/>.</param>
public sealed record WorkPlanDto(
    int Id,
    string PlanNumber,
    string PartNumber,
    string PartName,
    string? Revision,
    int Status,
    int LotSize,
    IReadOnlyList<OperationDto> Operations,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    string ConcurrencyStamp);

/// <summary>
/// A production order. The routing snapshot is carried as the stored JSON string
/// rather than a parsed shape: the client replays it through exactly the same
/// deserializer the server used, which is the only way the two hosts can be shown
/// to schedule the identical input.
/// </summary>
/// <param name="Id">Server-assigned key. Zero on a create request.</param>
/// <param name="OrderNumber">Unique order identifier.</param>
/// <param name="WorkPlanId">The routing this order was created from.</param>
/// <param name="WorkPlanNumber">That routing's plan number; ignored on write.</param>
/// <param name="Quantity">Pieces to make.</param>
/// <param name="ReleaseUtc">Earliest moment work may start.</param>
/// <param name="DueUtc">When the customer expects it.</param>
/// <param name="Priority">1 (normal) to 5 (rush).</param>
/// <param name="Status">0 draft, 1 released, 2 cancelled.</param>
/// <param name="RoutingRevision">The plan revision frozen at release.</param>
/// <param name="RoutingSnapshotJson">The frozen routing, empty until release.</param>
/// <param name="CreatedUtc">When the order was created.</param>
/// <param name="ModifiedUtc">When it last changed.</param>
/// <param name="ConcurrencyStamp">Opaque version token; see <see cref="WorkCenterDto.ConcurrencyStamp"/>.</param>
public sealed record ProductionOrderDto(
    int Id,
    string OrderNumber,
    int WorkPlanId,
    string? WorkPlanNumber,
    int Quantity,
    DateTime ReleaseUtc,
    DateTime DueUtc,
    int Priority,
    int Status,
    string RoutingRevision,
    string RoutingSnapshotJson,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    string ConcurrencyStamp);

/// <summary>The plant-wide working-time settings; a single row.</summary>
/// <param name="State">Two-letter German state code, deciding the public holidays.</param>
/// <param name="IncludePartialHolidays">Also close on days that are holidays only in parts of the state.</param>
/// <param name="AllowExtendedDay">ArbZG §3 sentence 2: the 10-hour day with averaging.</param>
/// <param name="AllowExtendedNight">ArbZG §6 (2): the 10-hour night shift with averaging.</param>
/// <param name="SundayWorkAllowed">ArbZG §10: the plant falls under a Sunday-work exception.</param>
/// <param name="HolidayWorkAllowed">ArbZG §10: the plant falls under a holiday-work exception.</param>
/// <param name="SundayBoundaryShiftHours">ArbZG §9 (2): hours the Sunday rest window is shifted, 0..6.</param>
/// <param name="MinimumRestHours">ArbZG §5: rest between working days, 10 or 11.</param>
/// <param name="ModifiedUtc">When the row last changed; ignored on write.</param>
public sealed record PlantSettingsDto(
    string State,
    bool IncludePartialHolidays,
    bool AllowExtendedDay,
    bool AllowExtendedNight,
    bool SundayWorkAllowed,
    bool HolidayWorkAllowed,
    int SundayBoundaryShiftHours,
    int MinimumRestHours,
    DateTime ModifiedUtc);

/// <summary>A closed period for one work center, on top of its repeating shift pattern.</summary>
/// <param name="Id">Server-assigned key. Zero on a create request.</param>
/// <param name="WorkCenterId">The work center that is closed.</param>
/// <param name="Start">First closed moment, plant-local wall-clock time.</param>
/// <param name="End">First moment work may resume.</param>
/// <param name="Kind">0 maintenance, 1 breakdown, 2 leave, 3 other — the working-time model's absence kinds.</param>
/// <param name="Label">Short label shown on the Gantt chart.</param>
public sealed record WorkCenterAbsenceDto(
    int Id,
    int WorkCenterId,
    DateTime Start,
    DateTime End,
    int Kind,
    string Label);
