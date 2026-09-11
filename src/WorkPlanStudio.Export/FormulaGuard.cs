namespace WorkPlanStudio.Export;

/// <summary>
/// Neutralises spreadsheet formula injection (CWE-1236).
/// <para>
/// A spreadsheet decides that a cell is a formula from its first character, and
/// it does so for text that arrived from a file just as readily as for text a
/// user typed. A part description of <c>=cmd|'/c calc'!A1</c> or
/// <c>@SUM(1+1)*cmd</c> is therefore executable content the moment the export
/// is opened. The data here is entered by the app's own users, which is exactly
/// the population that would be targeted: the planner exports, the works
/// manager opens.
/// </para>
/// <para>
/// The fix is a leading apostrophe, which every spreadsheet reads as "the rest
/// is literal text" and does not display. Escaping or stripping the character
/// would change the value; quoting does not help, because quotes are a CSV
/// construct the spreadsheet removes before it looks at the first character.
/// Only text is at risk - a number cell is written as a number and never
/// re-parsed.
/// </para>
/// </summary>
public static class FormulaGuard
{
    // Tab and carriage return are in the list because a leading one is stripped
    // by Excel before the formula test, which would hand the next character -
    // an '=' - the decision.
    private static readonly char[] Triggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>True when a spreadsheet would treat <paramref name="value"/> as a formula.</summary>
    public static bool LooksLikeFormula(string? value) =>
        !string.IsNullOrEmpty(value) && Array.IndexOf(Triggers, value[0]) >= 0;

    /// <summary>
    /// Returns <paramref name="value"/> unchanged, or prefixed with an
    /// apostrophe when a spreadsheet would otherwise evaluate it.
    /// </summary>
    public static string Neutralise(string value) =>
        LooksLikeFormula(value) ? "'" + value : value;
}
