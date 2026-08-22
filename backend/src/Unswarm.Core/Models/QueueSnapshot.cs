namespace Unswarm.Core.Models;

public sealed class QueueSnapshot
{
    /// <summary>Oldest currently-processing item (compatibility view of <see cref="Processing"/>).</summary>
    public QueueItem? CurrentSlot { get; init; }

    /// <summary>All in-flight items across every runtime lane.</summary>
    public IReadOnlyList<QueueItem> Processing { get; init; } = [];

    public IReadOnlyList<QueueItem> Waiting { get; init; } = [];
    public IReadOnlyList<QueueItem> RecentCompleted { get; init; } = [];
    public IReadOnlyList<ModelTransition> ActiveTransitions { get; init; } = [];

    /// <summary>Total skip budget consumed across all lanes.</summary>
    public int SkipsUsed { get; init; }

    /// <summary>Remaining skip budget under the active settings (0 when skip is disabled).</summary>
    public int SkipsRemaining { get; init; }
}
