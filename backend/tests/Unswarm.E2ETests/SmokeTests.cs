using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// Smoke test: the real API host boots in-memory with faked externals and the
/// authenticated queue snapshot endpoint responds with the documented contract.
/// </summary>
public sealed class SmokeTests
{
    [Fact]
    public async Task ApiHost_Boots_AndQueueSnapshotEndpointResponds()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient();

        // /api/queue/snapshot is [Authorize] (cookie control plane): log in as the
        // seeded admin and replay the auth cookie manually (secure cookies are not
        // auto-stored by HttpClientHandler over http).
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = UnswarmApiFactory.AdminUsername,
            password = UnswarmApiFactory.AdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(".Unswarm.Auth=", StringComparison.Ordinal));
        var cookiePair = setCookie.Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookiePair);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await client.GetAsync("/api/queue/snapshot", timeoutCts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutCts.Token)).RootElement;
        Assert.True(json.TryGetProperty("processing", out _), "snapshot must expose 'processing'");
        Assert.True(json.TryGetProperty("waiting", out _), "snapshot must expose 'waiting'");
        Assert.True(json.TryGetProperty("recentCompleted", out _), "snapshot must expose 'recentCompleted'");
        Assert.True(json.TryGetProperty("skipsUsed", out _), "snapshot must expose 'skipsUsed'");
        Assert.True(json.TryGetProperty("skipsRemaining", out _), "snapshot must expose 'skipsRemaining'");

        // Fresh host: empty queue.
        Assert.Equal(0, json.GetProperty("processing").GetArrayLength());
        Assert.Equal(0, json.GetProperty("waiting").GetArrayLength());
    }
}
