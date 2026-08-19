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
