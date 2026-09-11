namespace WorkPlanStudio.Api.Http;

/// <summary>Response headers that cost nothing and remove whole classes of mistake.</summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Adds the headers to every response.
    /// <para>
    /// An API that only ever returns JSON has no business being framed, sniffed
    /// or rendered, so the policy is the restrictive one: nothing may load, the
    /// document may not be embedded, and no referrer leaves. The one exception is
    /// the Development documentation page, which is a real HTML page with its own
    /// styles and scripts and is skipped here.
    /// </para>
    /// </summary>
    /// <param name="app">The pipeline to add to.</param>
    /// <param name="skipPathPrefixes">Paths whose responses keep the framework defaults, e.g. the doc UI.</param>
    public static IApplicationBuilder UseSecurityHeaders(
        this IApplicationBuilder app,
        params string[] skipPathPrefixes)
    {
        ArgumentNullException.ThrowIfNull(app);
        var skip = skipPathPrefixes ?? [];

        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (!skip.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Resource-Policy"] = "same-site";
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

                // Nothing here needs a camera, a microphone or a location, and
                // saying so is cheaper than proving it later.
                headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), microphone=(), payment=()";
            }

            await next(context);
        });
    }
}
