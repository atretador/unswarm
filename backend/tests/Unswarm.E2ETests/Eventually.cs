namespace Unswarm.E2ETests;

/// <summary>
/// Eventual-state polling helper (replicated from Unswarm.Tests Unit/Eventually).
/// Scheduler state is mutated from background lane-runner threads; poll instead of
/// asserting immediately or sleeping.
/// </summary>
public static class Eventually
{
    public static Task UntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        int pollMs = 25)
        => UntilAsync(() => Task.FromResult(condition()), timeout, pollMs);

    /// <summary>
    /// Polls an async condition until it returns true or the deadline expires.
    /// Never weakens assertions — it only replaces fixed sleeps and immediate
    /// reads of cross-thread state. Bounded so nothing can hang CI.
    /// </summary>
    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        int pollMs = 25)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Expected condition was not met within the timeout.");
            await Task.Delay(pollMs).ConfigureAwait(false);
        }
    }
}
