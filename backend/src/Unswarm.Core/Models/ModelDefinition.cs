namespace Unswarm.Core.Models;

public sealed class ModelDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Family { get; init; } = string.Empty;
    public string ParameterSize { get; init; } = string.Empty;
    public string Quantization { get; init; } = string.Empty;
    public ModelStatus Status { get; init; } = ModelStatus.Validating;
    public int ContextWindow { get; init; }
    public string ContainerImage { get; init; } = string.Empty;
    public string? SourceRuntimeId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
