using System.Net;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// Scenario 5 — unknown model fail-fast: a request for a model with no
/// registered-runtime mapping fails immediately with an HTTP error, the queue
/// snapshot shows the item terminal with the naming error message, and no
/// container was ever started.
/// </summary>
public sealed class UnknownModelFailFastE2ETests
{
    [Fact]
    public async Task UnknownModel_ReturnsError_MarksItemFailed_NeverStartsContainer()
    {
        await using var factory = new UnswarmApiFactory();
        // One valid runtime registered — proves the failure is specific to the
        // unknown model, not a broken registry.
        await factory.RegisterRuntimeAsync("runtime-a", "model-a");
        var inference = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        var startedAt = DateTime.UtcNow;

        // ── Act ───────────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await inference.PostChatCompletionAsync("ghost-model");

        // ── Assert: fail-fast HTTP error (controller maps dispatch failure to 502)
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.True(body.TryGetProperty("error", out _));

        // Fail-fast: sub-second turnaround, no retry backoff.
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(10),
            "unknown-model request must fail fast");

        // Snapshot shows the item terminal with an error naming the missing mapping.
        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.AnyItem(snap, "recentCompleted",
                i => TestClient.ModelOf(i) == "ghost-model"
                    && TestClient.StatusIs(i, "failed")
                    && (i.GetProperty("errorMessage").GetString() ?? "").Contains("not mapped"));
        });

        // No container lifecycle activity at all.
        Assert.Empty(factory.HostDocker.EventLog);
        Assert.Equal(0, factory.Inference.CallCount);
    }
}
