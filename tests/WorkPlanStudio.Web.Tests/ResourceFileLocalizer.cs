using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Localization;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The shipped resource file, read straight off disk and formatted the way the
/// app formats it.
/// <para>
/// <see cref="PassThroughLocalizer{T}"/> answers with the key, which is exactly
/// right for asking "does this page render the rule catalogue" and useless for
/// asking "what number does it print": a key has no placeholders, so every
/// argument disappears. The working-time page's whole point is the numbers, so
/// these tests read the real strings. A key that is missing comes back with
/// <see cref="LocalizedString.ResourceNotFound"/> set, so a test can assert on
/// that rather than on a silent fallback.
/// </para>
/// </summary>
internal sealed class ResourceFileLocalizer<T> : IStringLocalizer<T>
{
    private readonly Dictionary<string, string> _values;

    public ResourceFileLocalizer(string fileName = "SharedResource.resx")
    {
        var path = Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Resources", fileName);
        _values = XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? "", StringComparer.Ordinal);
    }

    /// <summary>Every key the file defines, for the completeness tests.</summary>
    public IReadOnlyCollection<string> Keys => _values.Keys;

    public LocalizedString this[string name] =>
        _values.TryGetValue(name, out var value)
            ? new LocalizedString(name, value, resourceNotFound: false)
            : new LocalizedString(name, name, resourceNotFound: true);

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var found = this[name];
            return found.ResourceNotFound
                ? found
                : new LocalizedString(name, string.Format(CultureInfo.CurrentUICulture, found.Value, arguments), false);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        _values.Select(pair => new LocalizedString(pair.Key, pair.Value, false));
}
