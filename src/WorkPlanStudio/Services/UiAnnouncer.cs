namespace WorkPlanStudio.Services;

/// <summary>
/// One place to say out loud what just happened.
/// <para>
/// Measured on the published 0.3.9: withdrawing a production order moved the row
/// to <em>Cancelled</em>, bumped the stored revision and moved focus to the page
/// heading — and the page held no live region at all, so a screen reader said
/// "Production Orders, heading": exactly what it says when the dialog is merely
/// dismissed. The application had learned in 0.3.8 to announce an <b>empty
/// list</b>; it never announced a <b>deleted row</b>.
/// </para>
/// <para>
/// A service rather than a component parameter because the region belongs to the
/// layout — one per page, outside whatever card or dialog the action happened in,
/// so it survives the dialog closing and the table re-rendering.
/// </para>
/// </summary>
public sealed class UiAnnouncer
{
    /// <summary>Raised with the sentence to announce. The layout's live region listens.</summary>
    public event Action<string>? Announced;

    /// <summary>Says <paramref name="message"/> in the page's live region. Empty messages are ignored.</summary>
    public void Say(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            Announced?.Invoke(message);
    }
}
