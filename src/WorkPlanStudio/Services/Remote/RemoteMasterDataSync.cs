using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services.Remote;

/// <summary>What a pull replaced.</summary>
/// <param name="CostCenters">Cost centers written.</param>
/// <param name="WorkCenters">Work centers written.</param>
/// <param name="WorkPlans">Work plans written.</param>
/// <param name="ProductionOrders">Production orders written.</param>
public sealed record RemotePullSummary(int CostCenters, int WorkCenters, int WorkPlans, int ProductionOrders);

/// <summary>
/// Fetches the server's master data into the in-browser database.
/// <para>
/// <b>One direction, and deliberately so.</b> This replaces the local copy with
/// the server's; it never sends local rows back. A two-way sync would need
/// change tracking, conflict resolution and a merge policy for a browser that
/// may have been offline for a week — a real project in its own right, and one
/// that would be dishonest to fake with a last-write-wins loop. Connected mode
/// therefore claims exactly this: read the server's data, run the schedule on
/// the server, and edit master data through the API rather than through the
/// local page. What is not solved is written down in ADR 0020 rather than left
/// for a user to discover.
/// </para>
/// <para>
/// Server identifiers are preserved rather than reassigned, because a released
/// order's frozen routing names work centers by id. Renumbering on import would
/// leave every snapshot pointing at the wrong machine.
/// </para>
/// </summary>
public sealed class RemoteMasterDataSync
{
    private readonly ApiClient _api;
    private readonly BrowserDatabase _db;

    /// <summary>Creates the service.</summary>
    /// <param name="api">The authenticated API client.</param>
    /// <param name="db">The in-browser database.</param>
    public RemoteMasterDataSync(ApiClient api, BrowserDatabase db)
    {
        _api = api;
        _db = db;
    }

    /// <summary>Replaces the local master data with the server's.</summary>
    /// <param name="cancellationToken">Cancels the fetch and the write.</param>
    /// <returns>What was written, or a failure the page can report.</returns>
    public async Task<ApplicationResult<RemotePullSummary>> PullAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CostCenterDto> costCenters;
        IReadOnlyList<WorkCenterDto> centers;
        IReadOnlyList<WorkCenterAbsenceDto> absences;
        IReadOnlyList<WorkPlanDto> plans;
        IReadOnlyList<ProductionOrderDto> orders;
        PlantSettingsDto? settings;

        try
        {
            costCenters = await _api.GetCostCentersAsync(cancellationToken);
            centers = await _api.GetWorkCentersAsync(cancellationToken);
            absences = await _api.GetAbsencesAsync(cancellationToken);
            plans = await _api.GetWorkPlansAsync(cancellationToken);
            orders = await _api.GetProductionOrdersAsync(cancellationToken);
            settings = await _api.GetPlantSettingsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            // The reason is on the network, not in the data; the page shows one
            // message either way and the raw exception text helps nobody.
            return new ApplicationResult<RemotePullSummary>(ApplicationResultStatus.PersistenceFailed);
        }

        // The cost centres the server actually has. A work centre naming one that
        // is not in this set arrives unassigned rather than with a cost centre
        // invented locally to satisfy the foreign key: "not assigned" is a state
        // Controlling recognises, a fabricated CC-2000 is not.
        var knownCostCenters = costCenters.Select(c => c.Id).ToHashSet();
        var knownWorkCenters = centers.Select(c => c.Id).ToHashSet();

        await using (var db = await _db.CreateContextAsync(cancellationToken))
        {
            // Order matters: the routing index references orders and work centres,
            // orders reference plans (Restrict), operations reference work centres
            // (Restrict) and work centres reference cost centres (Restrict).
            db.OrderRoutingCenters.RemoveRange(await db.OrderRoutingCenters.ToListAsync(cancellationToken));
            db.ProductionOrders.RemoveRange(await db.ProductionOrders.ToListAsync(cancellationToken));
            db.Operations.RemoveRange(await db.Operations.ToListAsync(cancellationToken));
            db.WorkPlans.RemoveRange(await db.WorkPlans.ToListAsync(cancellationToken));
            db.WorkCenterAbsences.RemoveRange(await db.WorkCenterAbsences.ToListAsync(cancellationToken));
            db.WorkCenters.RemoveRange(await db.WorkCenters.ToListAsync(cancellationToken));
            db.CostCenters.RemoveRange(await db.CostCenters.ToListAsync(cancellationToken));
            db.PlantSettings.RemoveRange(await db.PlantSettings.ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);

            db.CostCenters.AddRange(costCenters.Select(ToEntity));
            db.WorkCenters.AddRange(centers.Select(center => ToEntity(center, knownCostCenters)));
            db.WorkCenterAbsences.AddRange(absences.Select(ToEntity));
            db.WorkPlans.AddRange(plans.Select(ToEntity));
            db.ProductionOrders.AddRange(orders.Select(order => ToEntity(order, knownWorkCenters)));
            if (settings is not null)
                db.PlantSettings.Add(ToEntity(settings));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new ApplicationResult<RemotePullSummary>(ApplicationResultStatus.PersistenceFailed);
            }
        }

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<RemotePullSummary>.Success(
                new RemotePullSummary(costCenters.Count, centers.Count, plans.Count, orders.Count))
            : new ApplicationResult<RemotePullSummary>(ApplicationResultStatus.PersistenceFailed);
    }

    private static CostCenter ToEntity(CostCenterDto dto) => new()
    {
        Id = dto.Id,
        Code = dto.Code,
        Name = dto.Name,
        Description = dto.Description,
        IsActive = dto.IsActive
    };

    private static WorkCenter ToEntity(WorkCenterDto dto, IReadOnlySet<int> knownCostCenters) => new()
    {
        Id = dto.Id,
        Code = dto.Code,
        Name = dto.Name,

        // The id, never the display fields. CostCenterCode and CostCenterName are
        // labels the server computed; building a CostCenter out of them would add
        // a row the server does not have and give it a local key nothing points at.
        CostCenterId = dto.CostCenterId is { } id && knownCostCenters.Contains(id) ? id : null,
        HourlyRate = dto.HourlyRate,
        ParallelCapacity = dto.ParallelCapacity,
        ShiftPatternKey = dto.ShiftPatternKey,
        IsActive = dto.IsActive
    };

    private static WorkCenterAbsence ToEntity(WorkCenterAbsenceDto dto) => new()
    {
        Id = dto.Id,
        WorkCenterId = dto.WorkCenterId,
        Start = PlantTime.Wall(dto.Start),
        End = PlantTime.Wall(dto.End),
        Kind = Enum.IsDefined((AbsenceKind)dto.Kind) ? (AbsenceKind)dto.Kind : AbsenceKind.Other,
        Label = dto.Label
    };

    private static WorkPlan ToEntity(WorkPlanDto dto) => new()
    {
        Id = dto.Id,
        PlanNumber = dto.PlanNumber,
        PartNumber = dto.PartNumber,
        PartName = dto.PartName,
        Revision = dto.Revision,
        Status = (WorkPlanStatus)dto.Status,
        LotSize = dto.LotSize,
        CreatedUtc = dto.CreatedUtc,
        ModifiedUtc = dto.ModifiedUtc,
        Operations = [.. dto.Operations.Select(operation => new Operation
        {
            WorkPlanId = dto.Id,
            OperationNumber = operation.OperationNumber,
            Description = operation.Description,
            WorkCenterId = operation.WorkCenterId,
            SetupTimeMinutes = operation.SetupTimeMinutes,
            TimePerPieceMinutes = operation.TimePerPieceMinutes,
            Remarks = operation.Remarks
        })]
    };

    private static ProductionOrder ToEntity(ProductionOrderDto dto, IReadOnlySet<int> knownWorkCenters)
    {
        var order = new ProductionOrder
        {
            Id = dto.Id,
            OrderNumber = dto.OrderNumber,
            WorkPlanId = dto.WorkPlanId,
            Quantity = dto.Quantity,

            // Wall clock on both sides of the wire. System.Text.Json reads an
            // offset-bearing string back as Local, which would make the stored
            // reading depend on the visitor's own zone; PlantTime.Wall drops the
            // claim and keeps the reading.
            ReleaseLocal = PlantTime.Wall(dto.ReleaseLocal),
            DueLocal = PlantTime.Wall(dto.DueLocal),
            Priority = dto.Priority,
            Status = (ProductionOrderStatus)dto.Status,
            RoutingRevision = dto.RoutingRevision,

            // Verbatim. The snapshot is the record of what the shop was told to
            // build; re-serializing it here would risk changing it.
            RoutingSnapshotJson = dto.RoutingSnapshotJson,
            CreatedUtc = dto.CreatedUtc,
            ModifiedUtc = dto.ModifiedUtc
        };

        // Rebuild the blob's index locally rather than pulling it: it is derived
        // data, and the local guards (deactivate, delete) are what it exists for.
        // Without it a pulled plant would let a planner retire a machine a
        // released order still needs — the exact hole the join table closed.
        //
        // A snapshot naming a work centre the pull did not bring gets no row: the
        // order is still kept and still reported the way it always was, whereas an
        // index row pointing nowhere would fail the whole write on the foreign key.
        if (order.Status == ProductionOrderStatus.Released
            && RoutingSnapshot.Deserialize(order.RoutingSnapshotJson) is { } snapshot)
        {
            order.RoutingCenters =
            [
                .. snapshot.WorkCenterIds
                    .Where(knownWorkCenters.Contains)
                    .Select(id => new OrderRoutingCenter { WorkCenterId = id })
            ];
        }

        return order;
    }

    private static PlantSettings ToEntity(PlantSettingsDto dto) => new()
    {
        Id = PlantSettings.SingletonId,
        State = dto.State,
        IncludePartialHolidays = dto.IncludePartialHolidays,
        AllowExtendedDay = dto.AllowExtendedDay,
        AllowExtendedNight = dto.AllowExtendedNight,
        SundayWorkAllowed = dto.SundayWorkAllowed,
        HolidayWorkAllowed = dto.HolidayWorkAllowed,
        SundayBoundaryShiftHours = dto.SundayBoundaryShiftHours,
        MinimumRestHours = dto.MinimumRestHours,
        ModifiedUtc = dto.ModifiedUtc
    };
}
