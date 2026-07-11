using System.Text.Json.Serialization;

namespace WorkPlanStudio.Services;

// Minimal OpenAI-compatible chat DTOs. A source-generated serializer context is
// used (rather than reflection) so serialization keeps working under the trimming
// that the Blazor WebAssembly publish applies.

internal sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("temperature")] double Temperature);

internal sealed record ChatChoice(
    [property: JsonPropertyName("message")] ChatMessage Message);

internal sealed record ChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

/// <summary>Trim-safe (de)serialization for the AI chat DTOs and the stored settings.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(AssistantSettings))]
internal sealed partial class AssistantJsonContext : JsonSerializerContext;
