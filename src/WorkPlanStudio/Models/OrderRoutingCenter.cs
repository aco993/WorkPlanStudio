namespace WorkPlanStudio.Models;

/// <summary>
/// One work centre that a released order's frozen routing still needs.
/// <para>
/// The routing snapshot is a JSON blob, so the work-centre ids inside it are
/// invisible to SQL: nothing could stop a planner deactivating or deleting a
/// machine that a live shop-floor order depends on, and the order then vanished
/// from the schedule with only a preparation error to show for it. These rows
/// lift those ids out of the blob, which buys two things the blob cannot give —
/// an indexed query for the service guards, and a real foreign key so the
/// database refuses the delete even if a guard is ever forgotten.
/// </para>
/// <para>
/// The rows exist exactly while the dependency does: written when the order is
/// released, removed when it is cancelled or deleted. The snapshot itself is
/// still the record of what the shop was told to build; this is only its index.
/// </para>
/// </summary>
public class OrderRoutingCenter
{
    public int ProductionOrderId { get; set; }

    public ProductionOrder? ProductionOrder { get; set; }

    public int WorkCenterId { get; set; }

    public WorkCenter? WorkCenter { get; set; }
}
