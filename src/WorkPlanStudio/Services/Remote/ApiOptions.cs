using Microsoft.Extensions.Configuration;

namespace WorkPlanStudio.Services.Remote;

/// <summary>
/// Whether this build talks to a server, and where.
/// <para>
/// Absent by default, and that is the point. The app is published as static
/// files to a host that cannot run a backend, so "no API" has to be the normal
/// case rather than a degraded one: with nothing configured the persona
/// switcher, the in-browser database and every service behave exactly as they
/// did before this existed. Configuring <c>Api:BaseAddress</c> in
/// <c>wwwroot/appsettings.json</c> is what turns the personas into accounts.
/// </para>
/// </summary>
/// <param name="BaseAddress">Absolute URL of the API, or <c>null</c> for offline mode.</param>
public sealed record ApiOptions(string? BaseAddress)
{
    /// <summary>Configuration section these options are read from.</summary>
    public const string SectionName = "Api";

    /// <summary>Offline: no server, no accounts, no network.</summary>
    public static ApiOptions Offline { get; } = new((string?)null);

    /// <summary>True when a usable absolute address is configured.</summary>
    public bool IsConnected => Address is not null;

    /// <summary>
    /// The parsed address, or <c>null</c> when none is configured or the
    /// configured value is not an absolute http(s) URL. A malformed address
    /// leaves the app offline rather than throwing on start-up: a typo in a
    /// deployment's settings file should not turn a working demo into a blank
    /// page.
    /// </summary>
    public Uri? Address { get; } =
        Uri.TryCreate(BaseAddress, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    /// <summary>Reads the options from the host's configuration.</summary>
    /// <param name="configuration">The app configuration, which on WebAssembly is fetched from <c>wwwroot/appsettings.json</c>.</param>
    public static ApiOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ApiOptions(configuration[$"{SectionName}:BaseAddress"]);
    }
}
