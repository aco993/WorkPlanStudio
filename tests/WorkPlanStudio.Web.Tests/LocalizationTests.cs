using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// A bilingual UI is only bilingual if the two resource files stay in step. A
/// missing German key does not fail the build and does not throw at runtime —
/// <c>IStringLocalizer</c> quietly falls back to the key name — so the symptom is
/// a raw "Sched_KpiMakespan" label in the German UI. These tests turn that into a
/// build failure.
/// </summary>
public class LocalizationTests
{
    private static readonly string ResourcesDirectory = LocateResources();

    private static string LocateResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "WorkPlanStudio", "Resources");
    }

    private static Dictionary<string, string> Read(string fileName)
    {
        var path = Path.Combine(ResourcesDirectory, fileName);
        Assert.True(File.Exists(path), $"missing resource file: {path}");

        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? "");
    }

    [Fact]
    public void German_defines_every_key_that_English_does()
    {
        var missing = Read("SharedResource.resx").Keys
            .Except(Read("SharedResource.de.resx").Keys)
            .Order()
            .ToArray();

        Assert.True(missing.Length == 0, $"missing German translations: {string.Join(", ", missing)}");
    }

    [Fact]
    public void German_defines_no_keys_that_English_lacks()
    {
        var extra = Read("SharedResource.de.resx").Keys
            .Except(Read("SharedResource.resx").Keys)
            .Order()
            .ToArray();

        Assert.True(extra.Length == 0, $"orphaned German keys: {string.Join(", ", extra)}");
    }

    [Fact]
    public void No_translation_is_left_empty()
    {
        foreach (var file in new[] { "SharedResource.resx", "SharedResource.de.resx" })
        {
            var blank = Read(file).Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToArray();
            Assert.True(blank.Length == 0, $"{file} has empty values: {string.Join(", ", blank)}");
        }
    }

    /// <summary>
    /// Format placeholders must line up, or <c>string.Format</c> throws at runtime
    /// in one language and not the other.
    /// </summary>
    [Fact]
    public void Placeholders_match_between_languages()
    {
        var english = Read("SharedResource.resx");
        var german = Read("SharedResource.de.resx");

        foreach (var (key, value) in english)
        {
            if (german.TryGetValue(key, out var translated))
                Assert.Equal(PlaceholderCount(value), PlaceholderCount(translated));
        }

        static int PlaceholderCount(string text) =>
            Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().Count();
    }

    [Fact]
    public void Every_key_used_in_a_component_exists_in_the_resources()
    {
        var english = Read("SharedResource.resx");
        var componentsRoot = Directory.GetParent(ResourcesDirectory)!.FullName;

        var used = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"L\[""(?<key>[A-Za-z0-9_]+)""\]")
                .Select(m => m.Groups["key"].Value))
            .Distinct()
            .Order()
            .ToArray();

        Assert.NotEmpty(used);
        var undefined = used.Except(english.Keys).ToArray();
        Assert.True(undefined.Length == 0, $"keys used but never defined: {string.Join(", ", undefined)}");
    }
}
