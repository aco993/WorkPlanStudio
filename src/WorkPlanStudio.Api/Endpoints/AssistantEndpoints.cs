using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Endpoints;

public static class AssistantEndpoints
{
    private const string SystemPrompt =
        "You are a production-scheduling assistant. Explain the supplied finite-capacity schedule facts " +
        "to a planner in concise bullet points. State the constraint, why jobs are late and what to try next. " +
        "Never invent numbers or facts.";

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assistant").RequireAuthorization("operator").RequireRateLimiting("ai");
        group.MapGet("/status", (IConfiguration config) =>
        {
            var enabled = IsEnabled(config);
            return Results.Ok(new AssistantStatusDto(enabled, enabled ? config["Assistant:Model"] : null));
        });
        group.MapPost("/narrate", NarrateAsync);
        return app;
    }

    private static async Task<IResult> NarrateAsync(
        AssistantNarrationRequest request,
        IHttpClientFactory clients,
        IConfiguration config,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(config))
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "AI narrator is not configured.");
        if (string.IsNullOrWhiteSpace(request.Facts) || request.Facts.Length > 16_000 ||
            string.IsNullOrWhiteSpace(request.Language) || request.Language.Length > 12)
            return Results.BadRequest(new ApiError("invalid_assistant_request", "Assistant input is invalid."));

        var endpoint = new Uri(config["Assistant:Endpoint"]!, UriKind.Absolute);
        var model = config["Assistant:Model"]!;
        var key = config["Assistant:ApiKey"]!;
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "chat/completions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(new ProviderRequest(model,
        [
            new("system", $"{SystemPrompt} Respond in language '{request.Language}'."),
            new("user", request.Facts)
        ], 0.2));

        try
        {
            using var response = await clients.CreateClient("assistant").SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "AI provider request failed.");
            var body = await response.Content.ReadFromJsonAsync<ProviderResponse>(cancellationToken);
            var content = body?.Choices?.FirstOrDefault()?.Message.Content;
            if (string.IsNullOrWhiteSpace(content))
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "AI provider returned no narration.");
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimStart('-', '*', '•', ' ', '\t')).Where(line => line.Length > 0).Take(12).ToArray();
            return Results.Ok(new AssistantNarrationDto(lines, $"{endpoint.Host} · {model}"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, title: "AI provider timed out.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "AI provider is unavailable.");
        }
    }

    private static bool IsEnabled(IConfiguration config) =>
        Uri.TryCreate(config["Assistant:Endpoint"], UriKind.Absolute, out var endpoint) &&
        endpoint.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(endpoint.UserInfo) &&
        !string.IsNullOrWhiteSpace(config["Assistant:Model"]) &&
        !string.IsNullOrWhiteSpace(config["Assistant:ApiKey"]);

    private sealed record ProviderMessage(string Role, string Content);
    private sealed record ProviderRequest(string Model, IReadOnlyList<ProviderMessage> Messages, double Temperature);
    private sealed record ProviderChoice(ProviderMessage Message);
    private sealed record ProviderResponse(IReadOnlyList<ProviderChoice>? Choices);
}
