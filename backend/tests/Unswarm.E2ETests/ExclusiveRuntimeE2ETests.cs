using System.Net;

namespace Unswarm.E2ETests;

/// <summary>
/// Scenario 3 — exclusive runtime gating: an exclusive runtime (empty
/// CanRunAlongWith) may only start once the target is quiet, and starting it stops
/// the incompatible containers that were running. Ordering is enforced with proxy
/// gates + snapshot polling (no sleeps), and container lifecycle ordering is read
/// from the fake docker controller's event log.
/// </summary>
public sealed class ExclusiveRuntimeE2ETests
{
    [Fact]
    public async Task ExclusiveRuntime_WaitsForDrain_StopsIncompatibleContainers()
    {
        await using var factory = new UnswarmApiFactory();
        // All three run exclusively on the host target: nothing may co-locate.
        await factory.RegisterRuntimeAsync("runtime-a", "model-a");
        await factory.RegisterRuntimeAsync("runtime-b", "model-b");
        await factory.RegisterRuntimeAsync("runtime-c", "model-c");
        var client = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        // ── Phase 1: A starts alone and holds the target ──────────────────────
        factory.Inference.Gate("model-a");
        var postA = client.PostChatCompletionAsync("model-a");

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "processing",
                i => TestClient.ModelOf(i) == "model-a" && TestClient.RuntimeIdOf(i) == "runtime-a");
        });

        // ── Phase 2: B arrives while A is in flight ───────────────────────────
        var postB = client.PostChatCompletionAsync("model-b");
        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "waiting",
                i => TestClient.ModelOf(i) == "model-b"
                    && TestClient.BlockedBy(i).Contains("runtime-a"));
        });

        // ── Phase 2b: C arrives after B is already queued (deterministic FIFO:
        // posting concurrently would leave server-side arrival order undefined).
        var postC = client.PostChatCompletionAsync("model-c");

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            var waiting = TestClient.Items(snap, "waiting").ToList();
            return waiting.Count == 2
                && waiting.Where(i => TestClient.ModelOf(i) == "model-b")
                    .All(i => TestClient.BlockedBy(i).Contains("runtime-a"))
                && waiting.Where(i => TestClient.ModelOf(i) == "model-c")
                    .All(i => TestClient.BlockedBy(i).Contains("runtime-a"));
        });

        // Neither has touched inference or docker yet.
        Assert.Equal(1, factory.Inference.CallCount);
        Assert.Single(factory.HostDocker.StartedContainerIds);

        // ── Phase 3: drain A → B must start (stopping A's container first) ────
        // Gate B so its instant inference doesn't race the processing snapshot.
        factory.Inference.Gate("model-b");
        factory.Inference.Release("model-a");
        Assert.Equal(HttpStatusCode.OK, (await postA.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "processing",
                i => TestClient.ModelOf(i) == "model-b" && TestClient.RuntimeIdOf(i) == "runtime-b");
        });

        // C must still be blocked while B runs.
        Assert.Equal(2, factory.Inference.CallCount);
        var midSnap = await control.GetSnapshotAsync();
        Assert.True(TestClient.AnyItem(midSnap, "waiting", i => TestClient.ModelOf(i) == "model-c"));

        // Container lifecycle so far: start A → stop A → start B.
        await Eventually.UntilAsync(() => Task.FromResult(factory.HostDocker.EventLog.Count >= 3));
        Assert.Equal(
            ["start:runtime-a:host-1", "stop:host-1", "start:runtime-b:host-2"],
            factory.HostDocker.EventLog.ToArray());

        // ── Phase 4: drain B → C starts only after B completes ────────────────
        factory.Inference.Gate("model-c");
        factory.Inference.Release("model-b");
        Assert.Equal(HttpStatusCode.OK, (await postB.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "processing",
                i => TestClient.ModelOf(i) == "model-c" && TestClient.RuntimeIdOf(i) == "runtime-c");
        });

        // ── Phase 5: everything drains clean ──────────────────────────────────
        factory.Inference.Release("model-c");
        Assert.Equal(HttpStatusCode.OK, (await postC.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.SnapshotShowsCompleted(snap, "model-a")
                && TestClient.SnapshotShowsCompleted(snap, "model-b")
                && TestClient.SnapshotShowsCompleted(snap, "model-c")
                && snap.GetProperty("processing").GetArrayLength() == 0;
        });

        // Full lifecycle: each exclusive switch stopped its predecessor's container.
        Assert.Equal(
        [
            "start:runtime-a:host-1", "stop:host-1", "start:runtime-b:host-2",
            "stop:host-2", "start:runtime-c:host-3"
        ], factory.HostDocker.EventLog.ToArray());
        Assert.Equal(3, factory.Stats.CompletionCount);
    }
}
