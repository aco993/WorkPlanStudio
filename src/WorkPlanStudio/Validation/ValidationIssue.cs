namespace WorkPlanStudio.Validation;

/// <summary>A structured business validation failure that the UI can localize.</summary>
public sealed record ValidationIssue(string Field, string MessageKey, params object[] Arguments);
