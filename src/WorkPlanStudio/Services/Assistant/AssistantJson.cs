using System.Text.Json.Serialization;

namespace WorkPlanStudio.Services;

// Minimal wire DTOs for the three provider APIs. A source-generated serializer
// context is used (rather than reflection) so serialization keeps working under
// the trimming that the Blazor WebAssembly publish applies.

// ----- OpenAI-compatible /chat/completions -----

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

// ----- Anthropic /v1/messages -----

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record AnthropicRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages);

internal sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text);

internal sealed record AnthropicResponse(
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock>? Content,
    [property: JsonPropertyName("stop_reason")] string? StopReason);

// ----- Gemini generateContent -----

internal sealed record GeminiPart(
    [property: JsonPropertyName("text")] string Text);

internal sealed record GeminiContent(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts);

internal sealed record GeminiRequest(
    [property: JsonPropertyName("system_instruction")] GeminiContent SystemInstruction,
    [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents);

internal sealed record GeminiCandidate(
    [property: JsonPropertyName("content")] GeminiContent? Content);

internal sealed record GeminiResponse(
    [property: JsonPropertyName("candidates")] IReadOnlyList<GeminiCandidate>? Candidates);

/// <summary>Trim-safe (de)serialization for the provider DTOs and the stored settings.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(AnthropicRequest))]
[JsonSerializable(typeof(AnthropicResponse))]
[JsonSerializable(typeof(GeminiRequest))]
[JsonSerializable(typeof(GeminiResponse))]
[JsonSerializable(typeof(AssistantSettings))]
internal sealed partial class AssistantJsonContext : JsonSerializerContext;
