using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public sealed class QueueSnapshotResponse
{
    public QueueItemResponse? CurrentSlot { get; set; }
    public List<QueueItemResponse> Waiting { get; set; } = [];
    public List<QueueItemResponse> RecentCompleted { get; set; } = [];
    public List<ModelTransitionResponse> ActiveTransitions { get; set; } = [];

    public static QueueSnapshotResponse FromSnapshot(QueueSnapshot s) => new()
    {
        CurrentSlot = s.CurrentSlot is null ? null : QueueItemResponse.FromItem(s.CurrentSlot),
        Waiting = s.Waiting.Select(QueueItemResponse.FromItem).ToList(),
        RecentCompleted = s.RecentCompleted.Select(QueueItemResponse.FromItem).ToList(),
        ActiveTransitions = s.ActiveTransitions.Select(ModelTransitionResponse.FromTransition).ToList()
    };
}

public sealed class QueueItemResponse
{
    public string Id { get; set; } = "";
    public string ModelRequested { get; set; } = "";
    public string? ModelAssigned { get; set; }
    public string? TargetId { get; set; }
    public QueueItemStatus Status { get; set; }
    public int Priority { get; set; }
    public int TokensRequested { get; set; }
    public int TokensGenerated { get; set; }
    public double PromptTokensPerSec { get; set; }
    public double GenerationTokensPerSec { get; set; }
    public long ElapsedMs { get; set; }
    public long WaitMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public static QueueItemResponse FromItem(QueueItem i) => new()
    {
        Id = i.Id,
        ModelRequested = i.ModelRequested,
        ModelAssigned = i.ModelAssigned,
        TargetId = i.TargetId,
        Status = i.Status,
        Priority = i.Priority,
        TokensRequested = i.TokensRequested,
        TokensGenerated = i.TokensGenerated,
        PromptTokensPerSec = i.PromptTokensPerSec,
        GenerationTokensPerSec = i.GenerationTokensPerSec,
        ElapsedMs = i.ElapsedMs,
        WaitMs = i.WaitMs,
        CreatedAt = i.CreatedAt,
        ErrorMessage = i.ErrorMessage
    };
}

public sealed class ModelTransitionResponse
{
    public string Id { get; set; } = "";
    public string FromModel { get; set; } = "";
    public string ToModel { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EstimatedCompletion { get; set; }

    public static ModelTransitionResponse FromTransition(ModelTransition t) => new()
    {
        Id = t.Id,
        FromModel = t.FromModel,
        ToModel = t.ToModel,
        Status = t.Status,
        StartedAt = t.StartedAt,
        EstimatedCompletion = t.EstimatedCompletion
    };
}
