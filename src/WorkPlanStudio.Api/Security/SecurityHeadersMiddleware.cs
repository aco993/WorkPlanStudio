namespace WorkPlanStudio.Api.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; " +
                                        "script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; style-src-attr 'none'; " +
                                        "img-src 'self' data:; connect-src 'self'; form-action 'self'";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Cross-Origin-Embedder-Policy"] = "require-corp";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        await next(context);
    }
}
