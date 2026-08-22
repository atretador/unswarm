using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public sealed class QueueSnapshotResponse
{
    /// <summary>Oldest processing item — backward-compatible view of <see cref="Processing"/>.</summary>
    public QueueItemResponse? CurrentSlot { get; set; }

    /// <summary>All in-flight items across every runtime lane.</summary>
    public List<QueueItemResponse> Processing { get; set; } = [];

    public List<QueueItemResponse> Waiting { get; set; } = [];
    public List<QueueItemResponse> RecentCompleted { get; set; } = [];
    public List<ModelTransitionResponse> ActiveTransitions { get; set; } = [];

    /// <summary>Total skip budget consumed across all lanes.</summary>
    public int SkipsUsed { get; set; }

    /// <summary>Remaining skip budget for the current settings (0 when skip is disabled).</summary>
    public int SkipsRemaining { get; set; }

    public static QueueSnapshotResponse FromSnapshot(QueueSnapshot s) => new()
    {
        CurrentSlot = s.CurrentSlot is null ? null : QueueItemResponse.FromItem(s.CurrentSlot),
        Processing = s.Processing.Select(QueueItemResponse.FromItem).ToList(),
        Waiting = s.Waiting.Select(QueueItemResponse.FromItem).ToList(),
        RecentCompleted = s.RecentCompleted.Select(QueueItemResponse.FromItem).ToList(),
        ActiveTransitions = s.ActiveTransitions.Select(ModelTransitionResponse.FromTransition).ToList(),
        SkipsUsed = s.SkipsUsed,
        SkipsRemaining = s.SkipsRemaining
    };
}

public sealed class QueueItemResponse
{
    public string Id { get; set; } = "";
    public string ModelRequested { get; set; } = "";
    public string? ModelAssigned { get; set; }
    public string? TargetId { get; set; }

    /// <summary>Registered runtime id of the lane serving this item (null until routed).</summary>
    public string? RuntimeId { get; set; }

    /// <summary>In-flight runtime ids currently blocking this waiting item (coexistence rules).</summary>
    public List<string> BlockedByRuntimeIds { get; set; } = [];

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
        RuntimeId = i.RuntimeId,
        BlockedByRuntimeIds = i.BlockedByRuntimeIds.ToList(),
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

    /// <summary>Registered runtime id whose lane performed this switch.</summary>
    public string? RuntimeId { get; set; }

    public string Status { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EstimatedCompletion { get; set; }

    public static ModelTransitionResponse FromTransition(ModelTransition t) => new()
    {
        Id = t.Id,
        FromModel = t.FromModel,
        ToModel = t.ToModel,
        RuntimeId = t.RuntimeId,
        Status = t.Status,
        StartedAt = t.StartedAt,
        EstimatedCompletion = t.EstimatedCompletion
    };
}
