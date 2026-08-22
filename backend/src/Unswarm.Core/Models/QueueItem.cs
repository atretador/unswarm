namespace Unswarm.Core.Models;

public sealed record QueueItem
{
    public required string Id { get; init; }
    public required string ModelRequested { get; init; }
    public string? TargetId { get; init; }
    public string? ModelAssigned { get; init; }
    public QueueItemStatus Status { get; init; } = QueueItemStatus.Waiting;
    public int Priority { get; init; }
    public int TokensRequested { get; init; }
    public int TokensGenerated { get; init; }
    public double PromptTokensPerSec { get; init; }
    public double GenerationTokensPerSec { get; init; }
    public long ElapsedMs { get; init; }
    public long WaitMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
