using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Http;

/// <summary>
/// Every failure this API reports is an RFC 9457 problem document, built here so
/// the shape cannot drift endpoint by endpoint.
/// <para>
/// Validation failures keep the application's own message keys
/// (<c>Val_QuantityRange</c> and friends) rather than English sentences. The
/// browser client already translates those keys into two languages; inventing a
/// second, server-side vocabulary would mean the same rule is worded differently
/// depending on which host enforced it.
/// </para>
/// </summary>
public static class Problems
{
    /// <summary>Problem type for a request that broke a business rule.</summary>
    public const string ValidationType = "https://workplanstudio.invalid/problems/validation";

    /// <summary>Problem type for a write that lost a race or hit a uniqueness rule.</summary>
    public const string ConflictType = "https://workplanstudio.invalid/problems/conflict";

    /// <summary>Problem type for a write refused because the stored row moved on.</summary>
    public const string ConcurrencyType = "https://workplanstudio.invalid/problems/concurrency";

    /// <summary>
    /// 400 with the failing fields and their message keys.
    /// </summary>
    /// <param name="issues">The business-rule failures, as the shared validators produced them.</param>
    /// <returns>A validation problem document.</returns>
    public static ValidationProblem Validation(IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        var errors = issues
            .GroupBy(issue => issue.Field, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(Describe).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return TypedResults.ValidationProblem(
            errors,
            detail: "One or more business rules were not satisfied.",
            type: ValidationType,
            title: "Validation failed");
    }

    /// <summary>400 for a single field, for the checks that are not in a shared validator.</summary>
    /// <param name="field">The offending field.</param>
    /// <param name="messageKey">The application's message key for the rule.</param>
    /// <param name="arguments">Values the key's format string expects.</param>
    public static ValidationProblem Validation(string field, string messageKey, params object[] arguments) =>
        Validation([new ValidationIssue(field, messageKey, arguments)]);

    /// <summary>409 for a uniqueness or lifecycle rule — a duplicate code, a released order being edited.</summary>
    /// <param name="field">The field the conflict is about.</param>
    /// <param name="messageKey">The application's message key.</param>
    public static ProblemHttpResult Conflict(string field, string messageKey) =>
        TypedResults.Problem(
            detail: $"{field}: {messageKey}",
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            type: ConflictType,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["field"] = field,
                ["messageKey"] = messageKey
            });

    /// <summary>
    /// 409 for a lost optimistic-concurrency race: the row was read, somebody else
    /// wrote it, and this write would have thrown the other one away.
    /// </summary>
    /// <param name="resource">The resource that moved on, e.g. "work-center".</param>
    public static ProblemHttpResult Concurrency(string resource) =>
        TypedResults.Problem(
            detail: "The stored record changed after it was read. Reload it and apply the change again.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Concurrent modification",
            type: ConcurrencyType,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["resource"] = resource });

    /// <summary>404 with a body, so a caller gets the same document shape it gets everywhere else.</summary>
    /// <param name="resource">What was not found, e.g. "work-plan".</param>
    public static ProblemHttpResult NotFound(string resource) =>
        TypedResults.Problem(
            detail: $"No {resource} with that identifier.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static string Describe(ValidationIssue issue) =>
        issue.Arguments.Length == 0
            ? issue.MessageKey
            : $"{issue.MessageKey}({string.Join(", ", issue.Arguments.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)))})";
}
