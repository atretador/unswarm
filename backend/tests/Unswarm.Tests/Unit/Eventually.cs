namespace Unswarm.Tests.Unit;

/// <summary>
/// Eventual-state polling helper for scheduler tests. Worker state (started/stopped
/// containers, snapshots) is mutated from background lane-runner threads; asserting
/// immediately after a request's Tcs completes can observe stale state. Poll instead.
/// </summary>
public static class Eventually
{
    /// <summary>
    /// Polls <paramref name="condition"/> every <paramref name="pollMs"/> until it
    /// returns true or the deadline expires. Does not weaken assertions — callers
    /// keep their regular asserts afterwards; this only replaces fixed sleeps and
    /// immediate post-completion reads of cross-thread state.
    /// </summary>
    public static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        int pollMs = 25)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Expected condition was not met within the timeout.");
            await Task.Delay(pollMs);
        }
    }
}
