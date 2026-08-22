using Unswarm.Core.Contracts;
using Unswarm.Core.Services.Benchmarks;

namespace Unswarm.Api.Dtos;

public sealed class PromptResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>Benchmark generation cap used when this prompt drives a run.</summary>
    public int MaxTokens { get; set; } = BenchmarkDefaults.MaxTokens;
    public int CurrentVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static PromptResponse From(PromptEntry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Text = e.Text,
        IsDefault = e.IsDefault,
        MaxTokens = e.MaxTokens,
        CurrentVersion = e.CurrentVersion,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };
}

public sealed class PromptUpsertRequest
{
    public string? Name { get; set; }
    public string? Text { get; set; }

    /// <summary>
    /// Optional benchmark generation cap. Null on create → default (256);
    /// null on update → keep existing. When set, must be within
    /// [<see cref="BenchmarkDefaults.MinPromptMaxTokens"/>, <see cref="BenchmarkDefaults.MaxPromptMaxTokens"/>].
    /// </summary>
    public int? MaxTokens { get; set; }
}

public sealed class PromptVersionResponse
{
    public string Id { get; set; } = "";
    public string PromptId { get; set; } = "";
    public int Version { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    public static PromptVersionResponse From(PromptVersion v) => new()
    {
        Id = v.Id,
        PromptId = v.PromptId,
        Version = v.Version,
        Text = v.Text,
        CreatedAt = v.CreatedAt
    };
}

public sealed class PromptRollbackRequest
{
    public int Version { get; set; }
}
