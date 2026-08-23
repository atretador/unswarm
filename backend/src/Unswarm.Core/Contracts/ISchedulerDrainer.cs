using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

/// <summary>
/// Query and drain interface for the scheduler's active-inference state.
/// Used by services that need to stop containers (coexistence enforcement,
/// idle shutdown) to avoid killing a container that is actively serving a request.
/// </summary>
public interface ISchedulerDrainer
{
    /// <summary>
    /// Waits until all inferences using the given container complete, or until
    /// <paramref name="timeout"/> expires. Returns true if the container is
    /// safe to stop (zero active inferences), false on timeout.
    /// </summary>
    Task<bool> DrainContainerAsync(string containerId, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Returns true if any lane on any target currently has active inferences
    /// whose resident container matches <paramref name="containerId"/>.
    /// </summary>
    bool HasActiveInferences(string containerId);

    /// <summary>
    /// The most recent time the scheduler recorded activity (request enqueue,
    /// start, or completion) for the given registered runtime, or null when the
    /// scheduler has seen no traffic for the runtime since process start.
    /// </summary>
    DateTime? GetLastActivityUtc(string runtimeId);

    /// <summary>
    /// True while <paramref name="runtimeId"/> still has work to serve: in-flight
    /// or queued requests on any of its lanes, or a hot conversation hold within
    /// the conversation-affinity dwell window. A runtime in this state must never
    /// be stopped by idle shutdown, no matter how long it has been running.
    /// </summary>
    bool HasPendingWork(string runtimeId);

    /// <summary>
    /// Stops the runtime serving <paramref name="runtimeId"/> through the
    /// scheduler (idle-shutdown path): refuses while the runtime still has work to
    /// serve, drains in-flight work that raced in after the caller's idle check,
    /// stops the serving container or script, and clears lane residency plus the
    /// target's tracked running entry so the next request re-resolves cleanly
    /// instead of hitting a dead container.
    /// </summary>
    /// <param name="runtimeId">Registered runtime id.</param>
    /// <param name="containerId">
    /// The live docker container id currently serving the runtime (from a fresh
    /// container listing); required for container runtimes, ignored for scripts.
    /// </param>
    /// <returns>
    /// <see cref="IdleStopResult.Stopped"/> when the runtime was stopped;
    /// <see cref="IdleStopResult.Busy"/> when it still has work to serve (do not
    /// stop it); <see cref="IdleStopResult.NotManaged"/> when the runtime is not
    /// scheduler-managed (no lane, or unknown to the registry) and the caller may
    /// stop the observed unit directly.
    /// </returns>
    Task<IdleStopResult> StopIdleRuntimeAsync(string runtimeId, string? containerId, CancellationToken ct);
}
