using WorkPlanStudio.Models;

namespace WorkPlanStudio.Validation;

/// <summary>Business rules shared by every cost-centre entry point.</summary>
public static class CostCenterValidator
{
    public const int MaxCodeLength = 20;
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 250;

    public static IReadOnlyList<ValidationIssue> Validate(CostCenter costCenter)
    {
        ArgumentNullException.ThrowIfNull(costCenter);
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(costCenter.Code))
            issues.Add(new(nameof(costCenter.Code), "Val_Required"));
        else if (costCenter.Code.Trim().Length > MaxCodeLength)
            issues.Add(new(nameof(costCenter.Code), "Val_MaxLength", MaxCodeLength));
        else if (Text.HasControlCharacters(costCenter.Code))
            issues.Add(new(nameof(costCenter.Code), "Val_SingleLine"));

        if (string.IsNullOrWhiteSpace(costCenter.Name))
            issues.Add(new(nameof(costCenter.Name), "Val_Required"));
        else if (costCenter.Name.Trim().Length > MaxNameLength)
            issues.Add(new(nameof(costCenter.Name), "Val_MaxLength", MaxNameLength));
        else if (Text.HasControlCharacters(costCenter.Name))
            issues.Add(new(nameof(costCenter.Name), "Val_SingleLine"));

        if (costCenter.Description?.Trim().Length > MaxDescriptionLength)
            issues.Add(new(nameof(costCenter.Description), "Val_MaxLength", MaxDescriptionLength));

        return issues;
    }
}
