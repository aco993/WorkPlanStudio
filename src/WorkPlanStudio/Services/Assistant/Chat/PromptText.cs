using System.Text;

namespace WorkPlanStudio.Services.Chat;

/// <summary>
/// Prepares database free text for a prompt. Part names, work-centre names and
/// order numbers are typed by a user and end up inside the highest-trust region
/// of the request, so they are treated the way any other untrusted string is
/// treated before it is embedded in a structured document: the characters that
/// carry structure are removed, and the length is bounded.
/// <para>
/// This does not make prompt injection impossible — a model reads prose, and
/// prose cannot be escaped the way SQL or HTML can. It removes the cheap attacks
/// (closing the fence, inventing a Markdown heading, smuggling a newline into a
/// one-line fact) and bounds the expensive ones. See
/// <c>docs/adr/0025-hostile-input-on-the-model-path.md</c>.
/// </para>
/// </summary>
internal static class PromptText
{
    /// <summary>Opening marker of the data region. Never appears inside it, because <c>&lt;</c> is removed.</summary>
    public const string FenceOpen = "<schedule_facts>";

    /// <summary>Closing marker of the data region.</summary>
    public const string FenceClose = "</schedule_facts>";

    /// <summary>Opening marker of the on-device answer region.</summary>
    public const string AnswerOpen = "<on_device_answer>";

    /// <summary>Closing marker of the on-device answer region.</summary>
    public const string AnswerClose = "</on_device_answer>";

    /// <summary>
    /// One field of database text as a single safe line: no control characters,
    /// no angle brackets (so no fence can be closed), no Markdown or code
    /// structure, and never longer than <paramref name="maxLength"/> characters,
    /// the last of which is an ellipsis when something was cut.
    /// </summary>
    public static string Field(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var sb = new StringBuilder(Math.Min(value.Length, maxLength) + 1);
        bool lastWasSpace = false;
        foreach (var c in value)
        {
            if (sb.Length >= maxLength - 1)
            {
                sb.Append('…');
                break;
            }

            // Whitespace of every kind — including the newline that would let a
            // one-line fact become two — collapses to a single space.
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0 && !lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (IsStructural(c))
                continue;

            sb.Append(c);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// A multi-line block (the on-device answer) that keeps its line breaks but
    /// cannot close a fence or open a Markdown heading, and never grows past
    /// <paramref name="maxLength"/>. The answer is assembled by this application
    /// from localized resources, but the numbers and names inside it come from
    /// the same database text, so it gets the same treatment.
    /// </summary>
    public static string Block(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var sb = new StringBuilder(Math.Min(value.Length, maxLength) + 1);
        foreach (var c in value)
        {
            if (sb.Length >= maxLength - 1)
            {
                sb.Append('…');
                break;
            }

            // The line break is the one piece of structure this block may keep:
            // the answer is a list, and a list on one line is unreadable.
            if (c is '\n')
            {
                sb.Append('\n');
                continue;
            }

            if (c is '\r' || char.IsControl(c) || IsStructural(c))
                continue;

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static bool IsStructural(char c) => c is '<' or '>' or '#' or '`';
}
