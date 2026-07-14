namespace WorkPlanStudio.Validation;

public sealed record ValidationIssue(string Field, string MessageKey, params object[] Arguments);
