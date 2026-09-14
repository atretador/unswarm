using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// E2E tests for LogsController: GET /api/logs (admin-only). Exercises the
/// full HTTP roundtrip through the real controller and the in-memory FakeLogStore.
/// </summary>
public sealed class LogsControllerE2ETests
{
    [Fact]
    public async Task GetLogs_Returns200WithLogEntryArray()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/logs", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(JsonValueKind.Array, json.ValueKind);

        // Background services enqueue entries during host startup, so the array
        // won't be empty. Verify the shape of each returned entry.
        foreach (var entry in json.EnumerateArray())
        {
            Assert.True(entry.TryGetProperty("id", out _));
            Assert.True(entry.TryGetProperty("timestamp", out _));
            Assert.True(entry.TryGetProperty("level", out _));
            Assert.True(entry.TryGetProperty("source", out _));
            Assert.True(entry.TryGetProperty("message", out _));
        }
    }

    [Fact]
    public async Task GetLogs_WithSourceFilter_Returns200WithEmptyArray()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/logs?source=scheduler", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task GetLogs_WithLevelFilter_Returns200WithEmptyArray()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/logs?level=error", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task GetLogs_WithoutAuth_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient(); // no auth

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/logs", cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetLogs_WithoutAdminRole_Returns403()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient();

        // Create a non-admin user via the admin client
        var adminClient = await factory.CreateControlClientAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var createResponse = await adminClient.PostAsJsonAsync("/api/users", new
        {
            username = "regular-user",
            password = "RegularPass123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        // Login as the non-admin user
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "regular-user",
            password = "RegularPass123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var setCookie = Assert.Single(loginResponse.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(".Unswarm.Auth=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);

        // Attempt GET as non-admin — should be 403
        var getResponse = await client.GetAsync("/api/logs", cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);
    }
}
