namespace Unswarm.Core.Models;

public sealed class QueueSnapshot
{
    public QueueItem? CurrentSlot { get; init; }
    public IReadOnlyList<QueueItem> Waiting { get; init; } = [];
    public IReadOnlyList<QueueItem> RecentCompleted { get; init; } = [];
    public IReadOnlyList<ModelTransition> ActiveTransitions { get; init; } = [];
}
