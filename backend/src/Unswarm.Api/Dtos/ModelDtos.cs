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
    public string? SourceRuntimeId { get; set; }
    public string? SourceRuntimeName { get; set; }
    public string? SourceRuntimeAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Origin { get; set; } = "fleet";
    public string? ProviderName { get; set; }

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
        SourceRuntimeId = d.SourceRuntimeId,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
        Origin = "fleet"
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

/// <summary>
/// One message of an interactive test-chat conversation (OpenAI chat format subset).
/// </summary>
public sealed class TestChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

/// <summary>
/// Request body for POST /api/models/test-chat — a single admin-driven chat turn
/// routed through the same inference pipeline as /v1/chat/completions.
/// </summary>
public sealed class TestChatRequest
{
    /// <summary>Model id: a fleet registry id or a "cloud/&lt;provider&gt;/&lt;model&gt;" id.</summary>
    public string Model { get; set; } = "";

    /// <summary>Conversation so far, oldest first. Must contain at least one message.</summary>
    public List<TestChatMessage> Messages { get; set; } = new();

    /// <summary>Optional system prompt prepended as the first message.</summary>
    public string? System { get; set; }

    /// <summary>When true (default), stream SSE deltas straight through.</summary>
    public bool Stream { get; set; } = true;

    /// <summary>Optional generation cap (clamped to 1..32768).</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Optional sampling temperature (clamped to 0..2).</summary>
    public double? Temperature { get; set; }
}
