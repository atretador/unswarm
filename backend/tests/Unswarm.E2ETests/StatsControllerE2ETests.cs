using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// E2E tests for StatsController: GET /api/stats (any auth). Exercises the
/// full HTTP roundtrip through the real controller, enriched by FakeStatsTracker,
/// FakeDockerController, and the real ModelRegistry backed by in-memory SQLite.
/// </summary>
public sealed class StatsControllerE2ETests
{
    [Fact]
    public async Task GetStats_Returns200WithExpectedFields()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/stats", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.True(json.TryGetProperty("totalRequests", out _));
        Assert.True(json.TryGetProperty("activeRequests", out _));
        Assert.True(json.TryGetProperty("avgLatencyMs", out _));
        Assert.True(json.TryGetProperty("totalTokensProcessed", out _));
        Assert.True(json.TryGetProperty("totalPromptTokensCached", out _));
        Assert.True(json.TryGetProperty("uptimeSeconds", out _));
        Assert.True(json.TryGetProperty("modelsLoaded", out _));
        Assert.True(json.TryGetProperty("containersRunning", out _));
        Assert.True(json.TryGetProperty("queueDepth", out _));
        Assert.True(json.TryGetProperty("requestsPerMinute", out _));
        Assert.True(json.TryGetProperty("errorsLast24h", out _));
        Assert.True(json.TryGetProperty("tokensPerSecond", out _));
        Assert.True(json.TryGetProperty("switchCount", out _));
        Assert.True(json.TryGetProperty("lastSwitchMs", out _));
        Assert.True(json.TryGetProperty("avgSwitchMs", out _));

        // Default FakeStatsTracker returns a zeroed summary; no containers
        Assert.Equal(0, json.GetProperty("totalRequests").GetInt64());
        Assert.Equal(0, json.GetProperty("activeRequests").GetInt32());
        Assert.Equal(0, json.GetProperty("modelsLoaded").GetInt32());
        Assert.Equal(0, json.GetProperty("containersRunning").GetInt32());
    }

    [Fact]
    public async Task GetStats_WithSeededSummary_ReturnsSeededValues()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        // Seed the fake tracker with known values
        factory.Stats.SummaryToReturn = new Core.Models.StatsSummary
        {
            TotalRequests = 42,
            ActiveRequests = 3,
            AvgLatencyMs = 123.45,
            TotalTokensProcessed = 9999,
            TotalPromptTokensCached = 500,
            UptimeSeconds = 7200,
            QueueDepth = 5,
            ErrorsLast24h = 2,
            SwitchCount = 7,
            LastSwitchMs = 45.6,
            AvgSwitchMs = 32.1
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/stats", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(42, json.GetProperty("totalRequests").GetInt64());
        Assert.Equal(3, json.GetProperty("activeRequests").GetInt32());
        Assert.Equal(123.45, json.GetProperty("avgLatencyMs").GetDouble(), 2);
        Assert.Equal(9999, json.GetProperty("totalTokensProcessed").GetInt64());
        Assert.Equal(500, json.GetProperty("totalPromptTokensCached").GetInt64());
        Assert.Equal(7200, json.GetProperty("uptimeSeconds").GetInt64());
        Assert.Equal(5, json.GetProperty("queueDepth").GetInt32());
        Assert.Equal(2, json.GetProperty("errorsLast24h").GetInt32());
        Assert.Equal(7, json.GetProperty("switchCount").GetInt32());
        Assert.Equal(45.6, json.GetProperty("lastSwitchMs").GetDouble(), 1);
        Assert.Equal(32.1, json.GetProperty("avgSwitchMs").GetDouble(), 1);

        // modelsLoaded and containersRunning come from live services, not the summary
        // FakeDockerController returns empty containers, real registry is empty
        Assert.Equal(0, json.GetProperty("modelsLoaded").GetInt32());
        Assert.Equal(0, json.GetProperty("containersRunning").GetInt32());
    }

    [Fact]
    public async Task GetStats_WithoutAuth_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient(); // no auth

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/stats", cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
