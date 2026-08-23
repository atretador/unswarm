using System.Net;

namespace Unswarm.E2ETests;

/// <summary>
/// Scenario 2 — lane coexistence: two runtimes on the same target that the
/// symmetric CoexistencePolicy allows to run together serve concurrent requests;
/// the queue snapshot shows BOTH items processing simultaneously mid-flight.
/// </summary>
public sealed class LaneCoexistenceE2ETests
{
    [Fact]
    public async Task CompatibleRuntimes_ProcessConcurrently_BothVisibleInSnapshot()
    {
        await using var factory = new UnswarmApiFactory();
        // Symmetric allow-lists (by image/display name) → may co-locate.
        await factory.RegisterRuntimeAsync("runtime-a", "model-a", canRunAlongWith: ["runtime-b-image"]);
        await factory.RegisterRuntimeAsync("runtime-b", "model-b", canRunAlongWith: ["runtime-a-image"]);
        var client = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        // Hold both requests inside the fake proxy so coexistence is observable.
        factory.Inference.Gate("model-a");
        factory.Inference.Gate("model-b");

        var postA = client.PostChatCompletionAsync("model-a");
        var postB = client.PostChatCompletionAsync("model-b");

        // ── Assert: both items appear in `processing` at the SAME time ────────
        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            var processing = TestClient.Items(snap, "processing").ToList();
            return processing.Count == 2
                && processing.Select(TestClient.RuntimeIdOf).Distinct().Count() == 2
                && processing.Any(i => TestClient.ModelOf(i) == "model-a")
                && processing.Any(i => TestClient.ModelOf(i) == "model-b");
        });

        // Both containers were started; neither was stopped by the other's switch.
        Assert.Equal(2, factory.HostDocker.StartedContainerIds.Count);
        Assert.Empty(factory.HostDocker.StoppedContainerIds);

        // ── Release and let both complete ─────────────────────────────────────
        factory.Inference.Release("model-a");
        factory.Inference.Release("model-b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var responses = await Task.WhenAll(postA.WaitAsync(cts.Token), postB.WaitAsync(cts.Token));
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);

        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.SnapshotShowsCompleted(snap, "model-a")
                && TestClient.SnapshotShowsCompleted(snap, "model-b");
        });

        var final = await control.GetSnapshotAsync();
        Assert.Equal(0, final.GetProperty("processing").GetArrayLength());
        Assert.Equal(0, final.GetProperty("waiting").GetArrayLength());
    }
}
