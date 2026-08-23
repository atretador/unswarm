using System.Net;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// Scenario 1 — OpenAI-compatible inference flow end-to-end:
/// HTTP POST /v1/chat/completions → API endpoint → scheduler dispatcher → runtime
/// lane → (faked) container lifecycle → buffered inference response, then queue
/// snapshot visibility of the completed item.
/// </summary>
public sealed class InferenceFlowE2ETests
{
    [Fact]
    public async Task ChatCompletion_FlowsThroughScheduler_AndAppearsInRecentCompleted()
    {
        await using var factory = new UnswarmApiFactory();
        await factory.RegisterRuntimeAsync("runtime-a", "model-a");
        var inference = factory.CreateInferenceClient();
        var control = await factory.CreateControlClientAsync();

        // ── Act: full inference round-trip over HTTP ──────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await inference.PostChatCompletionAsync("model-a");

        // ── Assert: OpenAI-shaped 200 response streamed back from the proxy ───
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bodyText = await response.Content.ReadAsStringAsync(cts.Token);
        using var body = JsonDocument.Parse(bodyText);
        Assert.Equal("chat.completion", body.RootElement.GetProperty("object").GetString());
        Assert.Equal("model-a", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello from model-a",
            body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(15, body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        // The proxy actually served the request.
        Assert.Equal(1, factory.Inference.CallCount);

        // ── Assert: queue snapshot eventually shows the item completed ────────
        await Eventually.UntilAsync(async () =>
        {
            var snap = await control.GetSnapshotAsync();
            return TestClient.SnapshotShowsCompleted(snap, "model-a");
        });

        // Nothing left in flight or waiting.
        var final = await control.GetSnapshotAsync();
        Assert.Equal(0, final.GetProperty("processing").GetArrayLength());
        Assert.Equal(0, final.GetProperty("waiting").GetArrayLength());

        // Stats + logs recorded through the real pipeline.
        await Eventually.UntilAsync(() => Task.FromResult(factory.Stats.CompletionCount >= 1));
        await Eventually.UntilAsync(() => Task.FromResult(
            factory.Logs.Entries.Any(e => e.Source == "proxy" && e.Message.Contains("Request complete: model=model-a"))));
    }
}
