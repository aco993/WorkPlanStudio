using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WorkPlanStudio.Services.Remote;

/// <summary>Registers connected mode, and only when a server is configured.</summary>
public static class RemoteServiceCollectionExtensions
{
    /// <summary>
    /// Reads <c>Api:BaseAddress</c> and, if it is there, swaps the demo persona
    /// for a real sign-in and the local scheduler for the server's.
    /// <para>
    /// Registered last, after everything in offline mode, so the two
    /// replacements are plain last-one-wins registrations rather than surgery on
    /// what came before. With nothing configured this method registers a single
    /// value object and changes no behaviour at all — which is what keeps the
    /// static, no-server deployment the default rather than a special case.
    /// </para>
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">The host configuration; on WebAssembly this comes from <c>wwwroot/appsettings.json</c>.</param>
    /// <returns>The options that were read, so a caller can log or assert on them.</returns>
    public static ApiOptions AddOptionalApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = ApiOptions.From(configuration);
        services.AddSingleton(options);

        if (options.Address is null)
            return options;

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IRefreshTokenStorage, SessionRefreshTokenStorage>();

        // Two clients on purpose. The auth client has no bearer handler, because
        // the handler renews tokens through it and a handler that depended on
        // itself would deadlock on the first expired token.
        services.AddScoped(_ => new AuthApi(new HttpClient { BaseAddress = options.Address }));
        services.AddScoped<TokenStore>();
        services.AddScoped(provider => new ApiClient(new HttpClient(
            new BearerTokenHandler(provider.GetRequiredService<TokenStore>(), new HttpClientHandler()))
        {
            BaseAddress = options.Address
        }));

        services.AddScoped<RemoteAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(
            provider => provider.GetRequiredService<RemoteAuthenticationStateProvider>());

        services.AddScoped<RemoteMasterDataSync>();
        services.AddScoped<IProductionScheduleService, RemoteScheduleService>();

        return options;
    }
}
