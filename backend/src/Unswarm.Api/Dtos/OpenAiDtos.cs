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
}

public sealed class OpenAiModelUnswarmInfo
{
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public int ContextWindow { get; set; }
    public string ContainerImage { get; set; } = "";
    public string Status { get; set; } = "";
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
