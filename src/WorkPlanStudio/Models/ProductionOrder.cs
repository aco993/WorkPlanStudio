namespace WorkPlanStudio.Models;

/// <summary>Where an order is in its life cycle.</summary>
public enum ProductionOrderStatus
{
    /// <summary>Being prepared. The routing is still read live from the work plan.</summary>
    Draft = 0,

    /// <summary>Released to the shop floor. The routing is frozen and the order is schedulable.</summary>
    Released = 1,

    /// <summary>Withdrawn. Keeps its snapshot for the record but is no longer scheduled.</summary>
    Cancelled = 2
}

/// <summary>
/// A quantity of a part to make by a date — what actually gets scheduled.
/// <para>
/// A <see cref="WorkPlan"/> is master data: it describes how a part is made in
/// general and may be edited at any time. Scheduling that directly is wrong,
/// because an edit would silently change work that is already on the shop floor.
/// An order therefore takes an immutable <b>snapshot</b> of the routing when it
/// is released, and the scheduler reads the snapshot, never the live plan.
/// </para>
/// <para>
/// It is also the first place the app has a real customer due date, which is what
/// makes <see cref="Scheduling.DueDateRule.Explicit"/> usable at all — until now
/// every target had to be derived from processing time.
/// </para>
/// </summary>
public class ProductionOrder
{
    public int Id { get; set; }

    /// <summary>Unique order identifier, e.g. "PO-2026-0042".</summary>
    public string OrderNumber { get; set; } = "";

    /// <summary>The routing this order was created from. Kept for traceability, not read when scheduling.</summary>
    public int WorkPlanId { get; set; }

    public WorkPlan? WorkPlan { get; set; }

    /// <summary>How many pieces to make. Replaces the work plan's lot size for this order.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Earliest moment work may start, in <b>plant-local wall-clock time</b>.
    /// <para>
    /// It was called <c>ReleaseUtc</c> and was never UTC: the page took the
    /// visitor's calendar date and relabelled its <see cref="DateTime.Kind"/>.
    /// The shift calendar, the §9(2) Sunday window and the holiday closures all
    /// read it as plant-local, so the name was the only thing that was wrong —
    /// and a name that lies is an offset bug waiting for the first person who
    /// adds a conversion. See <see cref="Services.PlantTime"/>.
    /// </para>
    /// </summary>
    public DateTime ReleaseLocal { get; set; }

    /// <summary>
    /// When the customer expects it, in plant-local wall-clock time. Drives the
    /// explicit due-date rule and every lateness KPI.
    /// </summary>
    public DateTime DueLocal { get; set; }

    /// <summary>Order importance, 1 (normal) to 5 (rush). Used as the weight in the weighted dispatch rule.</summary>
    public int Priority { get; set; } = 1;

    public ProductionOrderStatus Status { get; set; } = ProductionOrderStatus.Draft;

    /// <summary>The work plan's revision at the moment of release, so the snapshot can be traced back.</summary>
    public string RoutingRevision { get; set; } = "";

    /// <summary>
    /// The routing as it was when this order was released, serialized. Empty until
    /// release. Deliberately a serialized blob rather than copied rows: it is
    /// never queried, only replayed, and copying the rows would invite someone to
    /// "fix" them later — which is exactly what the snapshot exists to prevent.
    /// </summary>
    public string RoutingSnapshotJson { get; set; } = "";

    /// <summary>
    /// The work centres <see cref="RoutingSnapshotJson"/> names, lifted out of the
    /// blob so the database can protect them. Present only while the order is
    /// released — see <see cref="OrderRoutingCenter"/>.
    /// </summary>
    public ICollection<OrderRoutingCenter> RoutingCenters { get; set; } = new List<OrderRoutingCenter>();

    /// <summary>A genuine UTC audit stamp, unlike the two dates above.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>A genuine UTC audit stamp, unlike the two dates above.</summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>A released order carries a frozen routing and can be scheduled.</summary>
    public bool IsSchedulable => Status == ProductionOrderStatus.Released && RoutingSnapshotJson.Length > 0;
}
