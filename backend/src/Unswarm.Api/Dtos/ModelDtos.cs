using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public sealed class LastBenchmarkResponse
{
    public double TokensPerSec { get; set; }
    public double LatencyMs { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    public static LastBenchmarkResponse From(BenchmarkHistoryEntry e) => new()
    {
        TokensPerSec = e.TokensPerSec,
        LatencyMs = e.LatencyMs,
        Timestamp = e.Timestamp
    };
}

public sealed class ModelResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public ModelStatus Status { get; set; }
    public LastBenchmarkResponse? LastBenchmark { get; set; }
    public int ContextWindow { get; set; }
    public string ContainerImage { get; set; } = "";
    public string? SourceContainerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static ModelResponse FromDefinition(ModelDefinition d) => FromDefinition(d, null);

    public static ModelResponse FromDefinition(ModelDefinition d, LastBenchmarkResponse? lastBenchmark) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Family = d.Family,
        ParameterSize = d.ParameterSize,
        Quantization = d.Quantization,
        Status = d.Status,
        LastBenchmark = lastBenchmark,
        ContextWindow = d.ContextWindow,
        ContainerImage = d.ContainerImage,
        SourceContainerId = d.SourceContainerId,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt
    };
}

public sealed class ModelCreateRequest
{
    public string Name { get; set; } = "";
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public int ContextWindow { get; set; }
    public string ContainerImage { get; set; } = "";
}

public sealed class ModelUpdateRequest
{
    public string? Name { get; set; }
    public string? Family { get; set; }
    public string? ParameterSize { get; set; }
    public string? Quantization { get; set; }
    public ModelStatus? Status { get; set; }
    public int? ContextWindow { get; set; }
    public string? ContainerImage { get; set; }
}
