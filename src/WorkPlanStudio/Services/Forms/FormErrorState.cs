using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;

namespace WorkPlanStudio.Services.Forms;

/// <summary>One message, and the control it belongs to when the page renders one.</summary>
/// <param name="Field">The validator's field name.</param>
/// <param name="InputId">The <c>id</c> of the control, or <c>null</c> when no control owns this message.</param>
/// <param name="Message">The localised sentence.</param>
/// <param name="Label">
/// The control's visible label, when the page named one. The summary puts it in
/// front of the message: without it two required fields produced two list items
/// both reading "Required", and two links with the same text pointing at
/// different controls (WCAG 2.4.4).
/// </param>
public sealed record FormError(string Field, string? InputId, string Message, string? Label = null);

/// <summary>
/// Where a form's validation messages live between a failed save and the render
/// that shows them.
/// <para>
/// It replaces a bare <c>Dictionary&lt;string, string&gt;</c> rendered at
/// hand-placed slots. Nothing checked that every issue found a slot, and four of
/// them had none — cost centre, active, part number and revision, plus the order's
/// routing — so pressing <b>Save</b> closed nothing, showed nothing and saved
/// nothing. The page could not even fall back to a banner, because its guard was
/// "no field errors", which is false exactly when a single unrenderable field
/// error is the whole problem.
/// </para>
/// <para>
/// Here a page <see cref="Declare"/>s the fields it actually renders. Anything
/// else that comes back from the validator is <em>placed in the summary</em>
/// rather than dropped, so a message can no longer have nowhere to go — and a
/// test can assert that a page renders a slot for every field its validator can
/// name.
/// </para>
/// <para>
/// It lives beside the services rather than under <c>Validation/</c> because it is
/// not a validator: it holds rendering state — control ids, a focus generation, a
/// localised sentence per slot — and so depends on the resource assembly and on
/// <see cref="ApplicationResult{T}"/>. <c>Validation/</c> is compiled into the API
/// as well, where neither of those exists, and keeping that folder free of UI
/// concerns is what lets the rules be shared instead of duplicated.
/// </para>
/// </summary>
public sealed class FormErrorState
{
    private readonly Dictionary<string, string> _inputIdByField = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _labelByField = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _messageByField = new(StringComparer.Ordinal);
    private readonly List<FormError> _all = [];

    /// <summary>Increments on every <see cref="Apply"/>, so the summary knows when to take focus again.</summary>
    public int Generation { get; private set; }

    /// <summary>Every message this form is showing, in the order the validator produced them.</summary>
    public IReadOnlyList<FormError> All => _all;

    /// <summary>Set when the save failed for a reason no field owns.</summary>
    public string? Summary { get; private set; }

    public bool HasAny => _all.Count > 0 || Summary is not null;

    /// <summary>Names a field this form renders a message slot for.</summary>
    /// <param name="field">The validator's field name.</param>
    /// <param name="inputId">The control's <c>id</c>.</param>
    /// <param name="label">
    /// The control's visible label. Optional only so that a form without one still
    /// compiles; a summary item without it says what is wrong and not where.
    /// </param>
    public void Declare(string field, string inputId, string? label = null)
    {
        _inputIdByField[field] = inputId;
        if (label is not null)
            _labelByField[field] = label;
    }

    /// <summary>The visible label a page declared for a field, if any.</summary>
    public string? LabelFor(string field) => _labelByField.GetValueOrDefault(field);

    public void Clear()
    {
        _messageByField.Clear();
        _all.Clear();
        Summary = null;
    }

    public bool Has(string field) => _messageByField.ContainsKey(field);

    /// <summary>The message for a field, or <c>null</c>.</summary>
    public string? this[string field] => _messageByField.GetValueOrDefault(field);

    /// <summary>For <c>aria-invalid</c>, which is a string attribute and not a flag.</summary>
    public string Invalid(string field) => Has(field) ? "true" : "false";

    /// <summary>For <c>aria-describedby</c>: the message element's id, or nothing to render.</summary>
    public string? DescribedBy(string field) => Has(field) ? ErrorId(field) : null;

    /// <summary>The id of a field's message element, derived from its control id so they cannot drift.</summary>
    public string ErrorId(string field) =>
        (_inputIdByField.TryGetValue(field, out var inputId) ? inputId : field) + "-error";

    /// <summary>For the <c>error</c> class on an input.</summary>
    public string? Style(string field) => Has(field) ? "error" : null;

    /// <summary>
    /// Reads a failed result into the form. Every issue lands somewhere: on its
    /// own control when the page declared one, in the summary otherwise.
    /// </summary>
    public void Apply<T>(
        ApplicationResult<T> result,
        IStringLocalizer<SharedResource> localizer,
        string? forbidden = null,
        string? notFound = null,
        string? persistenceFailed = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);

        Clear();
        Generation++;

        foreach (var issue in result.ValidationIssues ?? [])
        {
            var message = localizer[issue.MessageKey, issue.Arguments].Value;
            var inputId = _inputIdByField.GetValueOrDefault(issue.Field);
            if (inputId is not null)
                _messageByField.TryAdd(issue.Field, message);

            _all.Add(new FormError(issue.Field, inputId, message, _labelByField.GetValueOrDefault(issue.Field)));
        }

        Summary = result.Status switch
        {
            ApplicationResultStatus.Forbidden => forbidden ?? localizer["Error_Forbidden"].Value,
            ApplicationResultStatus.NotFound => notFound ?? localizer["Error_NotFound"].Value,
            ApplicationResultStatus.PersistenceFailed => persistenceFailed ?? localizer["Error_SaveFailed"].Value,
            ApplicationResultStatus.StorageFull => localizer["Error_StorageFull"].Value,
            ApplicationResultStatus.ChangedElsewhere => localizer["Error_ChangedElsewhere"].Value,
            _ => _all.Count == 0 ? localizer["Error_ActionFailed"].Value : null
        };
    }

    /// <summary>Puts one sentence in the summary, for a failure that never reached a validator.</summary>
    public void Fail(string message)
    {
        Clear();
        Generation++;
        Summary = message;
    }
}
