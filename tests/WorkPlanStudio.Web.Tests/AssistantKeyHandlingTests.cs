using System.Text.Json;
using System.Text.RegularExpressions;
using WorkPlanStudio.Services.Chat;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// What a browser-only application can honestly promise about somebody's API
/// key: that it goes into the header the provider expects and nowhere else, that
/// it is never written into a log, a URL or the shared settings value, that one
/// provider's key never travels to another provider's host, and that it can be
/// forgotten in one action. It cannot promise secrecy from script on the origin,
/// which is why that is stated in the UI rather than tested away — see
/// <c>docs/adr/0025-hostile-input-on-the-model-path.md</c>.
/// </summary>
public class AssistantKeyHandlingTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private const string Secret = "sk-live-do-not-leak-4711";

    // ----- on the wire -----

    [Theory]
    [InlineData(AssistantProvider.OpenAiCompatible, "Authorization")]
    [InlineData(AssistantProvider.Anthropic, "x-api-key")]
    [InlineData(AssistantProvider.Gemini, "x-goog-api-key")]
    public async Task The_key_travels_in_a_header_and_in_nothing_else(AssistantProvider providerKind, string header)
    {
        var transport = new RecordingHttpMessageHandler(ValidFor(providerKind));
        var provider = ChatProviders.Create(new HttpClient(transport), Hostile.Configured(providerKind, Secret));

        await provider.CompleteAsync("sys", [new ChatTurn(ChatRole.User, "q")], Ct);

        Assert.Contains(Secret, transport.LastRequest!.Headers.GetValues(header).Single(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, transport.LastRequest.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, transport.LastRequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, provider.Label, StringComparison.Ordinal);
    }

    // ----- in storage -----

    [Fact]
    public async Task The_key_is_stored_under_its_own_provider_scoped_name()
    {
        var browser = new FakeBrowserSettings();
        var service = new AssistantSettingsService(browser);

        await service.SaveAsync(Hostile.Configured(AssistantProvider.Gemini, Secret), Ct);

        Assert.Equal(Secret, browser.Store["assistant.key.Gemini"]);

        // The shared settings value keeps no key and no longer even has a place
        // to put one, so it can be read or exported without leaking a secret.
        using var stored = JsonDocument.Parse(browser.Store["assistant"]);
        Assert.DoesNotContain(Secret, browser.Store["assistant"], StringComparison.Ordinal);
        Assert.False(stored.RootElement.TryGetProperty("ApiKey", out _));
        Assert.Equal(2, stored.RootElement.GetProperty("Provider").GetInt32());
    }

    [Fact]
    public async Task Switching_provider_does_not_send_one_providers_key_to_another_host()
    {
        var browser = new FakeBrowserSettings();
        var service = new AssistantSettingsService(browser);
        await service.SaveAsync(Hostile.Configured(AssistantProvider.OpenAiCompatible, Secret), Ct);

        // The dialog switches provider and saves without retyping a key.
        await service.SaveAsync(Hostile.Configured(AssistantProvider.Gemini, "") with { Model = "gemini-2.5-flash" }, Ct);
        var loaded = await service.LoadAsync(Ct);

        Assert.False(loaded.HasApiKey);
        Assert.False(loaded.IsConfigured);                                  // Gemini has no key of its own yet
        Assert.Equal(Secret, browser.Store["assistant.key.OpenAiCompatible"]);
        Assert.DoesNotContain(Secret, browser.Store.GetValueOrDefault("assistant.key.Gemini", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_without_a_key_keeps_the_stored_one_so_the_dialog_never_has_to_hold_it()
    {
        var browser = new FakeBrowserSettings();
        var service = new AssistantSettingsService(browser);
        await service.SaveAsync(Hostile.Configured(AssistantProvider.Anthropic, Secret), Ct);

        await service.SaveAsync(Hostile.Configured(AssistantProvider.Anthropic, "") with { Model = "claude-opus-5" }, Ct);
        var loaded = await service.LoadAsync(Ct);

        Assert.Equal(Secret, loaded.ApiKey);
        Assert.Equal("claude-opus-5", loaded.Model);
    }

    [Fact]
    public async Task Forgetting_the_key_removes_it_from_the_browser_and_leaves_the_rest()
    {
        var browser = new FakeBrowserSettings();
        var service = new AssistantSettingsService(browser);
        await service.SaveAsync(Hostile.Configured(AssistantProvider.Anthropic, Secret), Ct);

        await service.ForgetApiKeyAsync(Ct);
        var loaded = await service.LoadAsync(Ct);

        Assert.False(loaded.HasApiKey);
        Assert.False(loaded.IsConfigured);
        Assert.True(loaded.Enabled);                                        // the rest of the settings survive
        Assert.DoesNotContain(Secret, string.Join("\n", browser.Store.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_left_in_an_older_settings_value_is_moved_out_of_it_on_first_load()
    {
        var browser = new FakeBrowserSettings();
        browser.Store["assistant"] =
            $$"""{"Enabled":true,"Provider":1,"Endpoint":"https://api.anthropic.com","Model":"claude-opus-5","ApiKey":"{{Secret}}"}""";
        var service = new AssistantSettingsService(browser);

        var loaded = await service.LoadAsync(Ct);

        Assert.Equal(Secret, loaded.ApiKey);                                // the user is not logged out of their own key
        Assert.Equal(Secret, browser.Store["assistant.key.Anthropic"]);
        Assert.DoesNotContain(Secret, browser.Store["assistant"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_settings_value_does_not_throw_and_yields_no_key()
    {
        var browser = new FakeBrowserSettings();
        browser.Store["assistant"] = "{not json at all";
        var service = new AssistantSettingsService(browser);

        var loaded = await service.LoadAsync(Ct);

        Assert.False(loaded.HasApiKey);
        Assert.False(loaded.Enabled);
    }

    // ----- in the source -----

    /// <summary>
    /// The mistakes that are easy to make later and invisible in a render: the
    /// key interpolated into a URL, or handed to a logger. In the style of
    /// <see cref="StaticAssetTests"/> — a grep is a poor test in general and the
    /// right one here, because the defect is a line of code that no test would
    /// otherwise execute.
    /// </summary>
    [Fact]
    public void No_source_line_puts_a_key_in_a_url_or_a_log()
    {
        var offenders = SourceLines()
            .Where(line => KeyMention.IsMatch(line.Text) && (UrlUse.IsMatch(line.Text) || LogUse.IsMatch(line.Text)))
            .Select(line => $"{line.File}:{line.Number}: {line.Text.Trim()}")
            .ToArray();

        Assert.True(offenders.Length == 0, "a key reaches a URL or a log:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void No_source_line_writes_a_key_to_the_console()
    {
        var offenders = SourceLines()
            .Where(line => KeyMention.IsMatch(line.Text) && line.Text.Contains("Console.", StringComparison.Ordinal))
            .Select(line => $"{line.File}:{line.Number}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join("\n", offenders));
    }

    private static readonly Regex KeyMention = new(@"\b(_apiKey|ApiKey|apiKey|x-api-key|x-goog-api-key)\b", RegexOptions.Compiled);
    private static readonly Regex UrlUse = new(@"RequestUri|HttpRequestMessage\(|\?key=|""https?:|Uri\.EscapeDataString|QueryString", RegexOptions.Compiled);
    private static readonly Regex LogUse = new(@"\bLog(Error|Warning|Information|Debug|Trace|Critical)\b|ILogger", RegexOptions.Compiled);

    private static IEnumerable<(string File, int Number, string Text)> SourceLines()
    {
        var root = Path.Join(RepoFiles.Root, "src", "WorkPlanStudio");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Join(root, "wwwroot", "js"), "*.js", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                yield return (Path.GetRelativePath(RepoFiles.Root, file), i + 1, lines[i]);
        }
    }

    private static string ValidFor(AssistantProvider provider) => provider switch
    {
        AssistantProvider.Anthropic => """{"content":[{"type":"text","text":"ok"}]}""",
        AssistantProvider.Gemini => """{"candidates":[{"content":{"parts":[{"text":"ok"}]}}]}""",
        _ => """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""
    };
}
