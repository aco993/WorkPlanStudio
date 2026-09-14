using System.Text;

namespace WorkPlanStudio.Export.Xlsx;

/// <summary>
/// Makes a sheet name Excel will accept.
/// <para>
/// Excel refuses to open a workbook whose sheet name breaks its rules, and it
/// does so with "we found a problem with some content" rather than anything
/// that names the sheet. Since the names here come from data - a work-center
/// name, an order number - sanitising is the right answer rather than throwing:
/// an export must not fail because somebody called a machine "Saw / Mill".
/// The one case with nothing to salvage, a name that is empty after trimming,
/// is rejected loudly.
/// </para>
/// </summary>
public static class SheetNames
{
    /// <summary>Excel's hard limit on a sheet name.</summary>
    public const int MaxLength = 31;

    // Excel reserves these for its own use inside formulas and references.
    private static readonly char[] Forbidden = ['[', ']', ':', '*', '?', '/', '\\'];

    /// <summary>True when Excel would accept <paramref name="name"/> as it stands.</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxLength
        && name.IndexOfAny(Forbidden) < 0
        && !name.StartsWith('\'')
        && !name.EndsWith('\'')
        && !name.Equals("History", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a name Excel accepts: forbidden characters replaced by a space,
    /// leading and trailing apostrophes removed, truncated to
    /// <see cref="MaxLength"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or only whitespace and punctuation Excel forbids.</exception>
    public static string Sanitise(string? name)
    {
        var builder = new StringBuilder(name?.Length ?? 0);
        foreach (var character in name ?? "")
            builder.Append(Array.IndexOf(Forbidden, character) >= 0 ? ' ' : character);

        var cleaned = builder.ToString().Trim().Trim('\'').Trim();
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength].TrimEnd();

        if (cleaned.Length == 0)
            throw new ArgumentException("A sheet name must contain at least one character Excel accepts.", nameof(name));

        // "History" is reserved; Excel silently owns it for change tracking.
        return cleaned.Equals("History", StringComparison.OrdinalIgnoreCase) ? cleaned + " 1" : cleaned;
    }

    /// <summary>
    /// Sanitises every name and makes the set unique, because Excel compares
    /// sheet names case-insensitively and two "Jobs" sheets are as fatal as an
    /// illegal character. Duplicates get a numeric suffix, truncating the base
    /// so the result still fits.
    /// </summary>
    public static IReadOnlyList<string> Unique(IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var name in names)
        {
            var candidate = Sanitise(name);
            if (!taken.Add(candidate))
            {
                for (int suffix = 2; ; suffix++)
                {
                    var tail = " (" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    var trimmed = candidate.Length + tail.Length > MaxLength
                        ? candidate[..(MaxLength - tail.Length)].TrimEnd()
                        : candidate;
                    var attempt = trimmed + tail;
                    if (taken.Add(attempt))
                    {
                        candidate = attempt;
                        break;
                    }
                }
            }

            result.Add(candidate);
        }

        return result;
    }
}
