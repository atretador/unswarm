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
}
