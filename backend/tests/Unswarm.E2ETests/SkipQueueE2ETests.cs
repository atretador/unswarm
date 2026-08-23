using System.Net;

namespace Unswarm.E2ETests;

/// <summary>
/// Scenario 4 — skip-queue flow with EnableParallelSlotSkip.
///
/// Semantics (LaneScheduler.IsStartable): a runtime that cannot coexist with
/// in-flight work never starts while that work runs — skip budget does NOT apply
/// there. Skip budget is consumed when a lane head starts OUT OF ORDER: i.e. it
/// bypasses another lane's head that is hard-blocked (typically by its own lane
/// capacity) or an earlier lane that still has pending work.
///
/// So the deterministic bypass scenario is: saturate runtime-a's lane to capacity
/// (MaxConcurrentInferences + 1 same-model requests → 1 processing, 1 waiting),
/// then send a compatible runtime-b request. With skip enabled, model-b skips past
/// the blocked earlier lane and processes concurrently, consuming budget; the
/// overflow request stays WAITING. With skip disabled, model-b must wait behind it.
/// </summary>
public sealed class SkipQueueE2ETests
{
    private static UnswarmApiFactory CreateFactory(bool enableSkip)
        => new(new Unswarm.Core.Models.Settings
        {
            EnableParallelSlotSkip = enableSkip,
            ParallelSlotSkipLimit = 3
        });

    [Fact]
    public async Task SkipEnabled_BypassesBlockedHead_ConsumesBudget_OverflowWaits()
    {
        await using var factory = CreateFactory(enableSkip: true);
        // Mutually compatible runtimes; lane A capacity 1.
        await factory.RegisterRuntimeAsync("runtime-a", "model-a", canRunAlongWith: ["runtime-b-image"]);
        await factory.RegisterRuntimeAsync("runtime-b", "model-b", canRunAlongWith: ["runtime-a-image"]);
        var client = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        // Saturate lane A: request 1 processing (gated), request 2 waiting on capacity.
        factory.Inference.Gate("model-a");
        var postA1 = client.PostChatCompletionAsync("model-a");

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "processing", i => TestClient.ModelOf(i) == "model-a");
        });

        var postA2 = client.PostChatCompletionAsync("model-a");

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "waiting", i => TestClient.ModelOf(i) == "model-a");
        });

        // Lane B arrives after lane A already has pending work → out-of-order start.
        factory.Inference.Gate("model-b");
        var postB = client.PostChatCompletionAsync("model-b");

        // ── Assert: model-b bypassed the blocked earlier lane and processes now ─
        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            var processing = TestClient.Items(snap, "processing").ToList();
            return processing.Count == 2
                && processing.Any(i => TestClient.ModelOf(i) == "model-a")
                && processing.Any(i => TestClient.ModelOf(i) == "model-b");
        });

        // The overflow request stays WAITING (lane capacity), not processing.
        var snapNow = await control.GetSnapshotAsync();
        Assert.True(TestClient.AnyItem(snapNow, "waiting",
            i => TestClient.ModelOf(i) == "model-a" && TestClient.RuntimeIdOf(i) == "runtime-a"));
        Assert.Equal(1, snapNow.GetProperty("waiting").GetArrayLength());

        // Skip budget: one bypass consumed of limit 3; remaining reflects budget.
        Assert.True(snapNow.GetProperty("skipsUsed").GetInt32() >= 1,
            $"skipsUsed={snapNow.GetProperty("skipsUsed").GetInt32()}");
        Assert.Equal(3 - snapNow.GetProperty("skipsUsed").GetInt32(),
            snapNow.GetProperty("skipsRemaining").GetInt32());

        // ── Drain everything ──────────────────────────────────────────────────
        factory.Inference.Release("model-a");
        factory.Inference.Release("model-b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var post in new[] { postA1, postA2, postB })
            Assert.Equal(HttpStatusCode.OK, (await post.WaitAsync(cts.Token)).StatusCode);

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.SnapshotShowsCompleted(snap, "model-a")
                && TestClient.SnapshotShowsCompleted(snap, "model-b")
                && snap.GetProperty("processing").GetArrayLength() == 0
                && snap.GetProperty("waiting").GetArrayLength() == 0;
        });
    }

    [Fact]
    public async Task SkipDisabled_CompatibleRequestStaysWaitingBehindBlockedLane()
    {
        await using var factory = CreateFactory(enableSkip: false);
        await factory.RegisterRuntimeAsync("runtime-a", "model-a", canRunAlongWith: ["runtime-b-image"]);
        await factory.RegisterRuntimeAsync("runtime-b", "model-b", canRunAlongWith: ["runtime-a-image"]);
        var client = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        factory.Inference.Gate("model-a");
        var postA1 = client.PostChatCompletionAsync("model-a");

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "processing", i => TestClient.ModelOf(i) == "model-a");
        });

        var postA2 = client.PostChatCompletionAsync("model-a");
        var postB = client.PostChatCompletionAsync("model-b");

        // Without skip, model-b may not bypass the blocked earlier lane — this
        // state is stable until release, so polling to it is deterministic.
        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return snap.GetProperty("waiting").GetArrayLength() == 2
                && TestClient.AnyItem(snap, "waiting", i => TestClient.ModelOf(i) == "model-a")
                && TestClient.AnyItem(snap, "waiting", i => TestClient.ModelOf(i) == "model-b");
        });

        var stableSnap = await control.GetSnapshotAsync();
        Assert.Equal(1, stableSnap.GetProperty("processing").GetArrayLength());
        Assert.Equal(0, stableSnap.GetProperty("skipsUsed").GetInt32());
        Assert.Equal(0, stableSnap.GetProperty("skipsRemaining").GetInt32()); // disabled → 0
        Assert.Equal(1, factory.Inference.CallCount); // only A1 invoked so far

        factory.Inference.Release("model-a");
        Assert.Equal(HttpStatusCode.OK, (await postA1.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);

        // Once lane A drains, A2 then B proceed normally.
        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.SnapshotShowsCompleted(snap, "model-a")
                && TestClient.SnapshotShowsCompleted(snap, "model-b")
                && snap.GetProperty("processing").GetArrayLength() == 0;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await postA2.WaitAsync(cts.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await postB.WaitAsync(cts.Token)).StatusCode);
    }
}
