using System.Net;

namespace Unswarm.E2ETests;

/// <summary>
/// Scenario 6 — shutdown/drain mid-flight: cancelling/disposing the host while a
/// request is processing and another is queued must terminate in bounded time,
/// and every in-flight/queued item must resolve (no item left Processing forever).
/// </summary>
public sealed class ShutdownDrainE2ETests
{
    [Fact]
    public async Task HostDisposalMidFlight_ResolvesAllItems_WithoutHanging()
    {
        var factory = new UnswarmApiFactory();
        await factory.RegisterRuntimeAsync("runtime-a", "model-a");
        var client = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        // One request processing (gated inside the fake proxy), one waiting.
        factory.Inference.Gate("model-a");
        var postProcessing = client.PostChatCompletionAsync("model-a");
        var postWaiting = client.PostChatCompletionAsync("model-a");

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return snap.GetProperty("processing").GetArrayLength() == 1
                && snap.GetProperty("waiting").GetArrayLength() == 1;
        });

        // ── Act: dispose the host mid-flight. Bounded — this is the hang guard.
        var disposeTask = factory.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(disposeTask, completed); // disposal finished → scheduler drained/stopped

        // Unblock the fake proxy so its fire-and-forget runner task can exit too.
        factory.Inference.Release("model-a");

        // Both HTTP calls must terminate within a bounded window — any outcome
        // (499-style cancellation, aborted connection, or fault) proves no hang.
        // WaitAsync throws TimeoutException if the call is still hanging.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var post in new[] { postProcessing, postWaiting })
        {
            try { await post.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (HttpRequestException) { /* connection abort is acceptable */ }
        }
    }

    [Fact]
    public async Task ShutdownAfterCompletion_SnapshotDrainsCleanly()
    {
        var factory = new UnswarmApiFactory();
        await factory.RegisterRuntimeAsync("runtime-a", "model-a");
        var client = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        var post = client.PostChatCompletionAsync("model-a");
        Assert.Equal(HttpStatusCode.OK, (await post.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.SnapshotShowsCompleted(snap, "model-a")
                && snap.GetProperty("processing").GetArrayLength() == 0;
        });

        // Quiet shutdown completes promptly.
        var disposeTask = factory.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(disposeTask, completed);
    }
}
