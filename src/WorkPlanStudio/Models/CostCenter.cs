namespace WorkPlanStudio.Models;

/// <summary>
/// An accounting cost centre ("Kostenstelle"): the unit Controlling books a work
/// centre's machine hours against.
/// <para>
/// It is master data owned elsewhere in the business, which is the whole reason
/// it is an entity rather than a string on <see cref="WorkCenter"/>. A work
/// centre <i>selects</i> a cost centre; it never invents one. As free text the
/// same centre arrives as <c>CC-2000</c>, <c>cc-2000</c> and <c>CC 2000</c>, and
/// the first question anyone asks of it — "what does CC-2000 cost per hour in
/// total?" — cannot be answered. See ADR 0016.
/// </para>
/// </summary>
public class CostCenter
{
    public int Id { get; set; }

    /// <summary>The controlling key, e.g. "CC-2000". Unique, compared case-insensitively.</summary>
    public string Code { get; set; } = "";

    /// <summary>What the cost centre is called, e.g. "Machining".</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional free-text note; nothing computes from it.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Retired cost centres stay in the database because work centres and past
    /// costings still point at them; they are only kept out of the pickers.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public ICollection<WorkCenter> WorkCenters { get; set; } = new List<WorkCenter>();

    /// <summary>How the picker and the tables label this row: code first, then name.</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Code : $"{Code} — {Name}";
}
