namespace WorkPlanStudio.Validation;

/// <summary>
/// One thing wrong with one field, as a resource key plus its arguments, so the
/// service layer never formats a sentence and the page never invents one.
/// </summary>
/// <param name="Field">The property the message belongs to. The page renders it next to that control.</param>
/// <param name="MessageKey">A key in <c>SharedResource</c>.</param>
/// <param name="Arguments">Format arguments for the message.</param>
public sealed record ValidationIssue(string Field, string MessageKey, params object[] Arguments)
{
    /// <summary>
    /// Compares the arguments by value, which the generated record equality does
    /// not: <c>object[]</c> has no structural equality, so two issues identical in
    /// every respect were unequal as soon as they carried an argument. That made
    /// the <c>Distinct()</c> in the work-plan validator deduplicate only the
    /// argument-less issues — the exact opposite of what it was there for, since
    /// the repeated ones are the per-operation range messages and all of those
    /// carry arguments.
    /// </summary>
    public bool Equals(ValidationIssue? other) =>
        other is not null
        && Field == other.Field
        && MessageKey == other.MessageKey
        && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Field);
        hash.Add(MessageKey);
        foreach (var argument in Arguments)
            hash.Add(argument);
        return hash.ToHashCode();
    }
}
