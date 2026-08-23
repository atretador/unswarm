namespace Unswarm.Core.Models;

/// <summary>
/// Outcome of a scheduler-managed idle stop (see
/// <c>ISchedulerDrainer.StopIdleRuntimeAsync</c>).
/// </summary>
public enum IdleStopResult
{
    /// <summary>The runtime's serving container/process was stopped.</summary>
    Stopped,

    /// <summary>
    /// The runtime still has work to serve (in-flight or queued) — the caller
    /// must NOT stop it.
    /// </summary>
    Busy,

    /// <summary>
    /// The runtime is not scheduler-managed (no lane, or unknown to the
    /// registry) — the caller may stop the observed unit directly.
    /// </summary>
    NotManaged
}
