using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Configurable ISchedulerDrainer fake for idle-shutdown tests: per-runtime
/// activity anchors and pending-work flags, plus a recorded stop-call log and an
/// optional result override for StopIdleRuntimeAsync.
/// </summary>
public sealed class FakeSchedulerDrainer : ISchedulerDrainer
{
    private readonly Dictionary<string, DateTime> _lastActivity = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingWork = new(StringComparer.Ordinal);

    /// <summary>Overrides the StopIdleRuntimeAsync outcome (default: Stopped).</summary>
    public Func<string, string?, IdleStopResult>? OnStopIdle { get; set; }

    /// <summary>Every StopIdleRuntimeAsync invocation: (runtimeId, containerId).</summary>
    public List<(string RuntimeId, string? ContainerId)> StopCalls { get; } = [];

    public void SetLastActivityUtc(string runtimeId, DateTime? utc)
    {
        if (utc.HasValue)
            _lastActivity[runtimeId] = utc.Value;
        else
            _lastActivity.Remove(runtimeId);
    }

    public void SetPendingWork(string runtimeId, bool pending)
    {
        if (pending)
            _pendingWork.Add(runtimeId);
        else
            _pendingWork.Remove(runtimeId);
    }

    public DateTime? GetLastActivityUtc(string runtimeId) =>
        _lastActivity.TryGetValue(runtimeId, out var utc) ? utc : null;

    public bool HasPendingWork(string runtimeId) => _pendingWork.Contains(runtimeId);

    public Task<bool> DrainContainerAsync(string containerId, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult(!HasActiveInferences(containerId));

    public bool HasActiveInferences(string containerId) => false;

    public Task<IdleStopResult> StopIdleRuntimeAsync(string runtimeId, string? containerId, CancellationToken ct)
    {
        StopCalls.Add((runtimeId, containerId));
        return Task.FromResult(OnStopIdle?.Invoke(runtimeId, containerId) ?? IdleStopResult.Stopped);
    }

    public void ForgetRuntime(string runtimeId) => _lastActivity.Remove(runtimeId);
}
