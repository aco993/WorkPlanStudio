namespace WorkPlanStudio.Validation;

/// <summary>Small text rules the validators share.</summary>
public static class Text
{
    /// <summary>
    /// True when a single-line field contains a line break or another control
    /// character.
    /// <para>
    /// Not cosmetics. Part names, work-centre names and order numbers are
    /// concatenated into the schedule assistant's system prompt, where a newline
    /// followed by a Markdown heading is structurally indistinguishable from the
    /// application's own sections. A name is a name; it has no lines.
    /// </para>
    /// </summary>
    public static bool HasControlCharacters(string? value) =>
        value is not null && value.Any(char.IsControl);

    /// <summary>
    /// Normalises a business key: trimmed and upper-cased.
    /// <para>
    /// The unique indexes on plan, order and work-centre numbers are
    /// <c>NOCASE</c>, so <c>wp-1009</c> and <c>WP-1009</c> are already the same
    /// key to the database. Storing one canonical spelling means the "next free
    /// number" suggester, the duplicate check and the index all read the value
    /// the same way.
    /// </para>
    /// </summary>
    public static string Key(string? value) => value?.Trim().ToUpperInvariant() ?? "";

    /// <summary>True when a decimal carries more than <paramref name="places"/> decimal places.</summary>
    /// <remarks>
    /// The columns used to declare <c>decimal(10,2)</c>, which SQLite ignores
    /// entirely — nothing anywhere enforced the scale the schema advertised, so an
    /// hourly rate of <c>78.129</c> was accepted, stored and costed with, while
    /// every screen rounded it to two places. The scale is enforced here because
    /// here is the only place it can be.
    /// </remarks>
    public static bool ExceedsScale(decimal value, int places) => decimal.Round(value, places) != value;
}
