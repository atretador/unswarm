using System.Text.Json.Serialization;

namespace Unswarm.Api.Dtos;

public sealed class OpenAiModelListResponse
{
    public string Object { get; set; } = "list";
    public List<OpenAiModelData> Data { get; set; } = [];
}

public sealed class OpenAiModelData
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "model";
    public long Created { get; set; }
    public string OwnedBy { get; set; } = "unswarm";
    public OpenAiModelUnswarmInfo Unswarm { get; set; } = new();

    /// <summary>Provider-specific context length (OpenRouter: context_length, vLLM: max_model_len).</summary>
    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }

    /// <summary>Max model length from vLLM/NIM.</summary>
    [JsonPropertyName("max_model_len")]
    public int? MaxModelLen { get; set; }

    /// <summary>Top provider info from OpenRouter.</summary>
    [JsonPropertyName("top_provider")]
    public OpenRouterTopProvider? TopProvider { get; set; }

    /// <summary>Human-readable name (OpenRouter, Zen, etc.).</summary>
    [JsonPropertyName("name")]
    public string? DisplayName { get; set; }
}

/// <summary>OpenRouter top_provider metadata.</summary>
public sealed class OpenRouterTopProvider
{
    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }
}

public sealed class OpenAiModelUnswarmInfo
{
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public int ContextWindow { get; set; }
    public int MaxOutputTokens { get; set; }
    public string ContainerImage { get; set; } = "";
    public string Status { get; set; } = "";
    [JsonPropertyName("supportedThinkingEfforts")]
    public string[]? SupportedThinkingEfforts { get; set; }
}

/// <summary>
/// Response from chatgpt.com/backend-api/codex/models — different shape from standard /v1/models.
/// </summary>
public sealed class CodexModelsResponse
{
    public List<CodexModelInfo> Models { get; set; } = [];
}

public sealed class CodexModelInfo
{
    public string Slug { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string DefaultReasoningLevel { get; set; } = "";
    public bool SupportedInApi { get; set; }
}
