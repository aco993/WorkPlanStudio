using System.Net;
using System.Net.Http.Headers;

namespace WorkPlanStudio.Services.Remote;

/// <summary>
/// Attaches the bearer token, and renews it once when the server says the token
/// is no longer good.
/// <para>
/// Renewing on a 401 rather than on a clock means the client does not have to
/// agree with the server about what time it is, and it is the only signal that
/// catches a token revoked before its expiry. The retry happens at most once
/// per request: if the renewed token is refused too, the session really is over
/// and looping would only turn that into a hang.
/// </para>
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly TokenStore _tokens;

    /// <summary>Creates the handler.</summary>
    /// <param name="tokens">The session this handler speaks for.</param>
    /// <param name="innerHandler">The transport underneath. On WebAssembly this is the browser's fetch bridge.</param>
    public BearerTokenHandler(TokenStore tokens, HttpMessageHandler innerHandler) : base(innerHandler) =>
        _tokens = tokens;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await _tokens.GetAccessTokenAsync(cancellationToken);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Buffered before the first attempt: a request message cannot be sent
        // twice, and the retry needs an identical copy of the body.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null)
            return response;

        var renewed = await _tokens.RenewAsync(token, cancellationToken);
        if (renewed is null)
            return response;

        response.Dispose();
        using var retry = Clone(request, body, renewed);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body, string token)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        foreach (var header in original.Headers)
        {
            if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        clone.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (original.Content?.Headers is { } headers)
            {
                foreach (var header in headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
