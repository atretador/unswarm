using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// E2E tests for SettingsController: GET /api/settings (any auth) and
/// PUT /api/settings (admin-only). Exercises the full HTTP roundtrip through
/// the real controller and the in-memory FakeSettingsStore.
/// </summary>
public sealed class SettingsControllerE2ETests
{
    [Fact]
    public async Task GetSettings_Returns200WithExpectedFields()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/settings", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.True(json.TryGetProperty("requestTimeout", out _));
        Assert.True(json.TryGetProperty("healthCheckInterval", out _));
        Assert.True(json.TryGetProperty("autoShutdownIdle", out _));
        Assert.True(json.TryGetProperty("idleTimeout", out _));
        Assert.True(json.TryGetProperty("logRetention", out _));
        Assert.True(json.TryGetProperty("priorityMode", out _));
        Assert.True(json.TryGetProperty("maxQueueDepth", out _));
        Assert.True(json.TryGetProperty("parallelSlotSkipLimit", out _));
        Assert.True(json.TryGetProperty("healthCheckTimeoutSeconds", out _));
        Assert.True(json.TryGetProperty("routerRetryAttempts", out _));
        Assert.True(json.TryGetProperty("routerRetryDelayMs", out _));

        // Verify defaults match Settings model
        Assert.Equal(120, json.GetProperty("requestTimeout").GetInt32());
        Assert.Equal(10, json.GetProperty("healthCheckInterval").GetInt32());
        Assert.False(json.GetProperty("autoShutdownIdle").GetBoolean());
        Assert.Equal("fifo", json.GetProperty("priorityMode").GetString());
        Assert.Equal(32, json.GetProperty("maxQueueDepth").GetInt32());
    }

    [Fact]
    public async Task PutSettings_ValidPayload_Returns200WithUpdatedValues()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var update = new
        {
            requestTimeout = 99,
            healthCheckInterval = 5,
            autoShutdownIdle = true,
            priorityMode = "priority",
            maxQueueDepth = 64,
            healthCheckTimeoutSeconds = 30,
            routerRetryAttempts = 2,
            routerRetryDelayMs = 500
        };

        var putResponse = await client.PutAsJsonAsync("/api/settings", update, cts.Token);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var putJson = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(99, putJson.GetProperty("requestTimeout").GetInt32());
        Assert.Equal(5, putJson.GetProperty("healthCheckInterval").GetInt32());
        Assert.True(putJson.GetProperty("autoShutdownIdle").GetBoolean());
        Assert.Equal("priority", putJson.GetProperty("priorityMode").GetString());
        Assert.Equal(64, putJson.GetProperty("maxQueueDepth").GetInt32());
        Assert.Equal(30, putJson.GetProperty("healthCheckTimeoutSeconds").GetInt32());
        Assert.Equal(2, putJson.GetProperty("routerRetryAttempts").GetInt32());
        Assert.Equal(500, putJson.GetProperty("routerRetryDelayMs").GetInt32());

        // Verify GET returns the updated values
        var getResponse = await client.GetAsync("/api/settings", cts.Token);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getJson = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(99, getJson.GetProperty("requestTimeout").GetInt32());
        Assert.Equal("priority", getJson.GetProperty("priorityMode").GetString());
    }

    [Fact]
    public async Task PutSettings_ClampsExtremeValues()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var update = new
        {
            maxQueueDepth = 99999,           // should clamp to 10000
            parallelSlotSkipLimit = 9999,    // should clamp to 1000
            queueStepsTillReset = 9999,      // should clamp to 1000
            healthCheckTimeoutSeconds = 999, // should clamp to 600
            routerRetryAttempts = 99,        // should clamp to 10
            routerRetryDelayMs = 99999,      // should clamp to 30000
            conversationDwellSeconds = 0     // should clamp to min 1
        };

        var putResponse = await client.PutAsJsonAsync("/api/settings", update, cts.Token);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var json = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(10000, json.GetProperty("maxQueueDepth").GetInt32());
        Assert.Equal(1000, json.GetProperty("parallelSlotSkipLimit").GetInt32());
        Assert.Equal(1000, json.GetProperty("queueStepsTillReset").GetInt32());
        Assert.Equal(600, json.GetProperty("healthCheckTimeoutSeconds").GetInt32());
        Assert.Equal(10, json.GetProperty("routerRetryAttempts").GetInt32());
        Assert.Equal(30000, json.GetProperty("routerRetryDelayMs").GetInt32());
        Assert.Equal(1, json.GetProperty("conversationDwellSeconds").GetInt32());
    }

    [Fact]
    public async Task PutSettings_ClampsLowExtremeValues()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var update = new
        {
            maxQueueDepth = -5,              // should clamp to 1
            parallelSlotSkipLimit = -10,     // should clamp to 1
            queueStepsTillReset = 0,         // should clamp to 1
            healthCheckTimeoutSeconds = 3,   // should clamp to 10
            routerRetryAttempts = -1,        // should clamp to 0
            routerRetryDelayMs = -100,       // should clamp to 0
            usageRetentionDays = -5          // should clamp to 0 (Math.Max(0, ...))
        };

        var putResponse = await client.PutAsJsonAsync("/api/settings", update, cts.Token);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var json = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(1, json.GetProperty("maxQueueDepth").GetInt32());
        Assert.Equal(1, json.GetProperty("parallelSlotSkipLimit").GetInt32());
        Assert.Equal(1, json.GetProperty("queueStepsTillReset").GetInt32());
        Assert.Equal(10, json.GetProperty("healthCheckTimeoutSeconds").GetInt32());
        Assert.Equal(0, json.GetProperty("routerRetryAttempts").GetInt32());
        Assert.Equal(0, json.GetProperty("routerRetryDelayMs").GetInt32());
        Assert.Equal(0, json.GetProperty("usageRetentionDays").GetInt32());
    }

    [Fact]
    public async Task PutSettings_WithoutAdminRole_Returns403()
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

        // Attempt PUT as non-admin — should be 403
        var putResponse = await client.PutAsJsonAsync("/api/settings", new
        {
            requestTimeout = 42
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, putResponse.StatusCode);
    }

    [Fact]
    public async Task GetSettings_WithoutAuth_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient(); // no auth

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/settings", cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
