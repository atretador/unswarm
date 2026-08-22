namespace Unswarm.Core.Services.Scheduler;

/// <summary>
/// Pure placement predicate for the lane scheduler. Decides whether the head item of a
/// <see cref="RuntimeLane"/> may start now. No channels, no clock, no I/O — the caller
/// supplies all state, which keeps the scheduling rules unit-testable.
/// </summary>
public static class LaneScheduler
{
    /// <summary>
    /// Decides whether the head item of <paramref name="lane"/> may start now.
    /// </summary>
    /// <param name="lane">Lane whose head is being considered.</param>
    /// <param name="inFlightRuntimeIds">
    /// Distinct runtime ids currently running work relevant to this decision
    /// (callers typically exclude the candidate's own runtime id — same-runtime
    /// parallelism is governed by capacity, not coexistence).
    /// </param>
    /// <param name="canCoexist">Symmetric CoexistencePolicy check: candidate vs in-flight id.</param>
    /// <param name="candidateIsExclusive">Candidate runtime runs alone (empty CanRunAlongWith).</param>
    /// <param name="laneHasCapacity">lane.ActiveInferences &lt; lane.MaxConcurrency.</param>
    /// <param name="isHeadOfItsLane">No earlier pending item in this lane.</param>
    /// <param name="bypassesBlockedItem">
    /// Starting while some other lane's head is blocked (by coexistence/exclusivity/capacity)
    /// or this item is not at the front of global queue order.
    /// </param>
    /// <param name="skipEnabled">SchedulerSettings.EnableParallelSlotSkip.</param>
    /// <param name="skipsRemaining">ParallelSlotSkipLimit - lane.SkipsUsed for THIS lane.</param>
    public static bool IsStartable(
        RuntimeLane lane,
        IReadOnlyList<string> inFlightRuntimeIds,
        Func<string, string, bool> canCoexist,
        bool candidateIsExclusive,
        bool laneHasCapacity,
        bool isHeadOfItsLane,
        bool bypassesBlockedItem,
        bool skipEnabled,
        int skipsRemaining)
    {
        ArgumentNullException.ThrowIfNull(lane);

        // Capacity is non-negotiable.
        if (!laneHasCapacity)
            return false;

        // Nothing in flight anywhere → free world; heads start immediately.
        if (inFlightRuntimeIds.Count == 0)
            return isHeadOfItsLane;

        // Exclusive runtimes never start alongside others.
        if (candidateIsExclusive)
            return false;

        // Non-exclusive candidates must coexist with EVERY in-flight runtime.
        foreach (var inFlight in inFlightRuntimeIds)
        {
            if (!canCoexist(lane.RuntimeId, inFlight))
                return false;
        }

        // Bypassing a blocked lane head consumes this lane's skip budget.
        if (bypassesBlockedItem && !(skipEnabled && skipsRemaining > 0))
            return false;

        return true;
    }
}
