using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Unswarm.Api.Controllers;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests the metrics analytics endpoints' filtering contract: legacy singular
/// provider/model parameters, multi-value providers/models sets (comma-joined
/// and repeated entries), their combination, and the summary endpoint's
/// groupBy comparison dimension.
/// </summary>
public sealed class MetricsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UnswarmDbContext _db;

    private static readonly DateTimeOffset WindowStart = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 1, 11, 0, 0, 0, TimeSpan.Zero);

    public MetricsControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new UnswarmDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Seeds one usage record; TimestampTicks is derived from the timestamp.</summary>
    private async Task SeedAsync(
        string provider,
        string model,
        int promptTokens = 100,
        int completionTokens = 50,
        long elapsedMs = 500,
        DateTimeOffset? timestamp = null,
        bool streaming = false,
        string? agent = null,
        string providerKind = "local")
    {
        var ts = timestamp ?? new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        _db.UsageRecords.Add(new UsageRecordEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = ts,
            TimestampTicks = ts.UtcTicks,
            Provider = provider,
            ProviderKind = providerKind,
            Agent = agent,
            Model = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CachedTokens = 0,
            IsStreaming = streaming,
            ElapsedMs = elapsedMs
        });
        await _db.SaveChangesAsync();
    }

    private MetricsController CreateController() =>
        new(_db, new FakeSettingsStore(), new NullLiveTailBroadcaster());

    // ─── Totals: multi-provider selection ────────────────────────────

    [Fact]
    public async Task GetTotals_MultiProviderSelection_SumsOnlySelectedProviders()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("anthropic", "claude-3-5-sonnet");
        await SeedAsync("local-agent", "llama-3");

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart,
            to: WindowEnd,
            providers: ["openai", "anthropic"],
            ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(3, totals.TotalRequests);
    }

    [Fact]
    public async Task GetTotals_CommaJoinedProviderValues_AreSplitIntoASet()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("anthropic", "claude-3-5-sonnet");
        await SeedAsync("local-agent", "llama-3");

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart,
            to: WindowEnd,
            // MVC binds `?providers=openai,anthropic` to a single array entry;
            // the controller must split it into distinct values itself.
            providers: ["openai,anthropic"],
            ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, totals.TotalRequests);
    }

    [Fact]
    public async Task GetTotals_MultiProvider_IgnoresWhitespaceAndEmptyEntries()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("local-agent", "llama-3");

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart,
            to: WindowEnd,
            providers: [" openai ", "", ","],
            ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, totals.TotalRequests);
    }

    // ─── Totals: singular/plural interaction + model filters ─────────

    [Fact]
    public async Task GetTotals_LegacySingularProvider_StillExactMatches()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("anthropic", "claude-3-5-sonnet");

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart, to: WindowEnd, provider: "openai", ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, totals.TotalRequests);
    }

    [Fact]
    public async Task GetTotals_MultiModelSelection_ExactMatches()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("openai", "gpt-4o-mini"); // substring of gpt-4o — must NOT match exact set
        await SeedAsync("openai", "o3");

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart,
            to: WindowEnd,
            models: ["gpt-4o", "o3"],
            ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, totals.TotalRequests);
    }

    [Fact]
    public async Task GetTotals_LegacySingularModel_KeepsSubstringSemantics()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("openai", "gpt-4o-mini");

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart, to: WindowEnd, model: "gpt-4o", ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        // Contains matches both gpt-4o and gpt-4o-mini.
        Assert.Equal(2, totals.TotalRequests);
    }

    [Fact]
    public async Task GetTotals_ProviderAndModelFilters_CombineWithAnd()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("openai", "o3");
        await SeedAsync("anthropic", "claude-3-5-sonnet");

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart,
            to: WindowEnd,
            provider: "openai",
            models: ["o3"],
            ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, totals.TotalRequests);
    }

    // ─── Models breakdown ─────────────────────────────────────────────

    [Fact]
    public async Task GetModels_MultiProviderSelection_ReturnsOnlySelectedProviders()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("anthropic", "claude-3-5-sonnet");
        await SeedAsync("local-agent", "llama-3");

        var controller = CreateController();
        var result = await controller.GetModels(
            from: WindowStart, to: WindowEnd, providers: ["openai", "local-agent"], ct: CancellationToken.None);

        var summaries = Assert.IsType<List<Unswarm.Api.Dtos.ModelUsageSummary>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, summaries.Count);
        Assert.All(summaries, s => Assert.NotEqual("anthropic", s.Provider));
    }

    // ─── Cost-unit attribution (local agent vs raw provider) ──────────

    [Fact]
    public async Task GetProviders_TwoLocalRuntimesWithSameAgent_CollapseIntoOneRow()
    {
        // Two distinct runtime display names on the same agent host must
        // aggregate into a single cost-unit row keyed by the agent.
        await SeedAsync("runtime-a", "llama-3", agent: "agent-x");
        await SeedAsync("runtime-b", "llama-3", agent: "agent-x");
        await SeedAsync("runtime-c", "llama-3", agent: "agent-y");

        var controller = CreateController();
        var result = await controller.GetProviders(
            from: WindowStart, to: WindowEnd, ct: CancellationToken.None);

        var providers = Assert.IsType<List<Unswarm.Api.Dtos.ProviderUsageSummary>>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, providers.Count);
        var collapsed = providers.Single(p => p.Provider == "agent-x");
        Assert.Equal(2, collapsed.RequestCount);
        Assert.Equal(200, collapsed.PromptTokens);
        Assert.DoesNotContain(providers, p => p.Provider is "runtime-a" or "runtime-b");
    }

    [Fact]
    public async Task GetProviders_NullAgentLocalRow_FallsBackToProviderDisplayName()
    {
        // Legacy local rows (recorded before the Agent column existed) have a
        // null agent and must fall back to the raw provider display name.
        await SeedAsync("runtime-legacy", "llama-3", agent: null);

        var controller = CreateController();
        var result = await controller.GetProviders(
            from: WindowStart, to: WindowEnd, ct: CancellationToken.None);

        var providers = Assert.IsType<List<Unswarm.Api.Dtos.ProviderUsageSummary>>(
            Assert.IsType<OkObjectResult>(result).Value);

        var row = Assert.Single(providers);
        Assert.Equal("runtime-legacy", row.Provider);
        Assert.Equal(1, row.RequestCount);
    }

    [Fact]
    public async Task GetProviders_CloudRowWithAgent_IsAttributedToCloudProviderNotAgent()
    {
        // The cost unit only substitutes the agent for ProviderKind == "local";
        // a stray/legacy Agent on a cloud row must be ignored.
        await SeedAsync("openai", "gpt-4o", providerKind: "cloud", agent: "agent-x");
        await SeedAsync("anthropic", "claude-3-5-sonnet", providerKind: "cloud", agent: "agent-x");

        var controller = CreateController();
        var result = await controller.GetProviders(
            from: WindowStart, to: WindowEnd, ct: CancellationToken.None);

        var providers = Assert.IsType<List<Unswarm.Api.Dtos.ProviderUsageSummary>>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, providers.Count);
        Assert.Contains(providers, p => p.Provider == "openai");
        Assert.Contains(providers, p => p.Provider == "anthropic");
        Assert.DoesNotContain(providers, p => p.Provider == "agent-x");
    }

    [Fact]
    public async Task GetTotals_ProviderFilter_MatchesCostUnitAgent()
    {
        await SeedAsync("runtime-a", "llama-3", agent: "agent-x");
        await SeedAsync("runtime-b", "llama-3", agent: "agent-x");
        await SeedAsync("runtime-c", "llama-3", agent: null);

        var controller = CreateController();
        var result = await controller.GetTotals(
            from: WindowStart, to: WindowEnd, provider: "agent-x", ct: CancellationToken.None);

        var totals = Assert.IsType<Unswarm.Api.Dtos.UsageTotalsResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, totals.TotalRequests);
    }

    [Fact]
    public async Task GetModels_LocalRowsWithSameAgentAndModel_CollapseIntoOneRow()
    {
        await SeedAsync("runtime-a", "llama-3", agent: "agent-x");
        await SeedAsync("runtime-b", "llama-3", agent: "agent-x");

        var controller = CreateController();
        var result = await controller.GetModels(
            from: WindowStart, to: WindowEnd, ct: CancellationToken.None);

        var summaries = Assert.IsType<List<Unswarm.Api.Dtos.ModelUsageSummary>>(
            Assert.IsType<OkObjectResult>(result).Value);

        var row = Assert.Single(summaries);
        Assert.Equal("agent-x", row.Provider);
        Assert.Equal(2, row.RequestCount);
    }

    // ─── Summary groupBy dimension ────────────────────────────────────

    [Fact]
    public async Task GetSummary_GroupByProvider_SplitsBucketsPerProvider()
    {
        // Two records land in the same hourly bucket; one on another provider.
        await SeedAsync("openai", "gpt-4o", promptTokens: 200, completionTokens: 100);
        await SeedAsync("openai", "gpt-4o", promptTokens: 100, completionTokens: 50);
        await SeedAsync("anthropic", "claude-3-5-sonnet", promptTokens: 40, completionTokens: 20);

        var controller = CreateController();
        var result = await controller.GetSummary(
            from: WindowStart,
            to: WindowEnd,
            groupBy: "provider",
            granularity: "day",
            ct: CancellationToken.None);

        var buckets = Assert.IsType<Unswarm.Api.Dtos.MetricsTimeBucket[]>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, buckets.Length); // one bucket per provider, same window

        var openai = buckets.Single(b => b.Group == "openai");
        Assert.Equal(2, openai.RequestCount);
        Assert.Equal(300, openai.PromptTokens);
        Assert.Equal(150, openai.CompletionTokens);

        var anthropic = buckets.Single(b => b.Group == "anthropic");
        Assert.Equal(1, anthropic.RequestCount);
        Assert.Equal(40, anthropic.PromptTokens);

        // Grouped rows must always carry a non-null group identity.
        Assert.All(buckets, b => Assert.NotNull(b.Group));
    }

    [Fact]
    public async Task GetSummary_GroupByModel_SplitsBucketsPerModel()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("openai", "o3");

        var controller = CreateController();
        var result = await controller.GetSummary(
            from: WindowStart,
            to: WindowEnd,
            groupBy: "model",
            granularity: "day",
            ct: CancellationToken.None);

        var buckets = Assert.IsType<Unswarm.Api.Dtos.MetricsTimeBucket[]>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, buckets.Length);
        Assert.NotNull(buckets.SingleOrDefault(b => b.Group == "gpt-4o"));
        Assert.NotNull(buckets.SingleOrDefault(b => b.Group == "o3"));
    }

    [Fact]
    public async Task GetSummary_WithoutGroupBy_RowsHaveNullGroupAndAggregateEverything()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("anthropic", "claude-3-5-sonnet");

        var controller = CreateController();
        var result = await controller.GetSummary(
            from: WindowStart, to: WindowEnd, granularity: "day", ct: CancellationToken.None);

        var buckets = Assert.IsType<Unswarm.Api.Dtos.MetricsTimeBucket[]>(
            Assert.IsType<OkObjectResult>(result).Value);

        var bucket = Assert.Single(buckets);
        Assert.Null(bucket.Group);
        Assert.Equal(2, bucket.RequestCount);
    }

    [Fact]
    public async Task GetSummary_GroupByAppliesAfterMultiProviderFilter()
    {
        await SeedAsync("openai", "gpt-4o");
        await SeedAsync("anthropic", "claude-3-5-sonnet");
        await SeedAsync("local-agent", "llama-3");

        var controller = CreateController();
        var result = await controller.GetSummary(
            from: WindowStart,
            to: WindowEnd,
            providers: ["openai", "anthropic"],
            groupBy: "provider",
            granularity: "day",
            ct: CancellationToken.None);

        var buckets = Assert.IsType<Unswarm.Api.Dtos.MetricsTimeBucket[]>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, buckets.Length);
        Assert.DoesNotContain(buckets, b => b.Group == "local-agent");
    }

    [Fact]
    public async Task GetSummary_GroupByProviderModel_SplitsBucketsByCostUnitAndModel()
    {
        // Two runtimes share agent-x (collapse to one provider); two models are
        // used, and llama-3 is served by both runtimes so its tokens must sum.
        await SeedAsync("runtime-a", "llama-3", promptTokens: 200, completionTokens: 100, agent: "agent-x");
        await SeedAsync("runtime-b", "llama-3", promptTokens: 100, completionTokens: 50, agent: "agent-x");
        await SeedAsync("runtime-c", "mistral", promptTokens: 40, completionTokens: 20, agent: "agent-x");
        await SeedAsync("runtime-d", "llama-3", promptTokens: 10, completionTokens: 5, agent: "agent-y");

        var controller = CreateController();
        var result = await controller.GetSummary(
            from: WindowStart,
            to: WindowEnd,
            groupBy: "provider_model",
            granularity: "day",
            ct: CancellationToken.None);

        var buckets = Assert.IsType<Unswarm.Api.Dtos.MetricsTimeBucket[]>(
            Assert.IsType<OkObjectResult>(result).Value);

        // 3 distinct (provider, model) pairs in the same day bucket.
        Assert.Equal(3, buckets.Length);
        Assert.All(buckets, b => Assert.Null(b.Group));
        Assert.All(buckets, b => Assert.NotNull(b.Provider));
        Assert.All(buckets, b => Assert.NotNull(b.Model));

        var xLlama = buckets.Single(b => b.Provider == "agent-x" && b.Model == "llama-3");
        Assert.Equal(2, xLlama.RequestCount);
        Assert.Equal(300, xLlama.PromptTokens);
        Assert.Equal(150, xLlama.CompletionTokens);

        var xMistral = buckets.Single(b => b.Provider == "agent-x" && b.Model == "mistral");
        Assert.Equal(1, xMistral.RequestCount);
        Assert.Equal(40, xMistral.PromptTokens);

        var yLlama = buckets.Single(b => b.Provider == "agent-y" && b.Model == "llama-3");
        Assert.Equal(1, yLlama.RequestCount);
        Assert.Equal(10, yLlama.PromptTokens);

        // The runtime display names must never surface as a provider.
        Assert.DoesNotContain(buckets, b => b.Provider is "runtime-a" or "runtime-b" or "runtime-c" or "runtime-d");
    }

    [Fact]
    public async Task GetSummary_GroupByProviderModel_ProviderAndModelPayloadsRemainNullOnOtherBranches()
    {
        await SeedAsync("openai", "gpt-4o");

        var controller = CreateController();
        var result = await controller.GetSummary(
            from: WindowStart,
            to: WindowEnd,
            groupBy: "provider",
            granularity: "day",
            ct: CancellationToken.None);

        var buckets = Assert.IsType<Unswarm.Api.Dtos.MetricsTimeBucket[]>(
            Assert.IsType<OkObjectResult>(result).Value);

        var bucket = Assert.Single(buckets);
        Assert.Equal("openai", bucket.Group);
        Assert.Null(bucket.Provider);
        Assert.Null(bucket.Model);
    }

    // ─── Latency bands ────────────────────────────────────────────────

    [Fact]
    public async Task GetLatencyBands_MultiProviderSelection_CountsOnlySelectedProviders()
    {
        await SeedAsync("openai", "gpt-4o", elapsedMs: 300);   // <500ms band
        await SeedAsync("anthropic", "claude", elapsedMs: 700); // 500ms-1s band
        await SeedAsync("local-agent", "llama-3", elapsedMs: 1200); // excluded by filter

        var controller = CreateController();
        var result = await controller.GetLatencyBands(
            from: WindowStart, to: WindowEnd, providers: ["openai", "anthropic"], ct: CancellationToken.None);

        var bands = Assert.IsType<Unswarm.Api.Dtos.LatencyBandResponse[]>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, bands[0].Count); // <500ms
        Assert.Equal(1, bands[1].Count); // 500ms-1s
        Assert.Equal(0, bands[2].Count); // 1-2s (the excluded local record)
    }

    // ─── Usage feed ───────────────────────────────────────────────────

    [Fact]
    public async Task GetUsage_MultiProviderSelection_PaginatesFilteredRows()
    {
        for (var i = 0; i < 3; i++)
        {
            await SeedAsync("openai", "gpt-4o");
        }
        await SeedAsync("anthropic", "claude-3-5-sonnet");

        var controller = CreateController();
        var result = await controller.GetUsage(
            from: WindowStart,
            to: WindowEnd,
            providers: ["openai"],
            page: 1,
            pageSize: 2,
            ct: CancellationToken.None);

        // The endpoint projects an anonymous {items,total,page,pageSize} shape;
        // reflect the total off the anonymous object.
        var ok = Assert.IsType<OkObjectResult>(result);
        var total = (int)ok.Value!.GetType().GetProperty("total")!.GetValue(ok.Value)!;
        Assert.Equal(3, total);
    }

    // ─── API-key usage ────────────────────────────────────────────────

    private async Task SeedKeyedAsync(
        string? keyId,
        string? keyName,
        string provider,
        string model,
        int promptTokens = 100,
        int completionTokens = 50,
        DateTimeOffset? timestamp = null,
        string providerKind = "local",
        string? agent = null)
    {
        var ts = timestamp ?? new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        _db.UsageRecords.Add(new UsageRecordEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = ts,
            TimestampTicks = ts.UtcTicks,
            Provider = provider,
            ProviderKind = providerKind,
            Agent = agent,
            Model = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ApiKeyId = keyId,
            ApiKeyName = keyName
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetApiKeyUsage_GroupsByKeyAndExcludesUnattributed()
    {
        await SeedKeyedAsync("k1", "alpha", "openai", "gpt-4o", promptTokens: 100, completionTokens: 50);
        await SeedKeyedAsync("k1", "alpha", "openai", "gpt-4o", promptTokens: 100, completionTokens: 50);
        await SeedKeyedAsync("k2", "beta", "anthropic", "claude", promptTokens: 10, completionTokens: 5);
        await SeedKeyedAsync(null, null, "openai", "gpt-4o"); // cookie/admin → excluded

        var result = await CreateController().GetApiKeyUsage(WindowStart, WindowEnd, CancellationToken.None);

        var keys = Assert.IsType<List<Unswarm.Api.Dtos.ApiKeyUsageSummary>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, keys.Count);
        var alpha = keys.Single(k => k.ApiKeyId == "k1");
        Assert.Equal(2, alpha.RequestCount);
        Assert.Equal(200, alpha.PromptTokens);
        Assert.Equal(100, alpha.CompletionTokens);
        Assert.Equal("alpha", alpha.KeyName);
    }

    [Fact]
    public async Task GetApiKeyUsage_NoKeyedRecords_ReturnsEmpty()
    {
        await SeedAsync("openai", "gpt-4o");

        var result = await CreateController().GetApiKeyUsage(WindowStart, WindowEnd, CancellationToken.None);

        var keys = Assert.IsType<List<Unswarm.Api.Dtos.ApiKeyUsageSummary>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(keys);
    }

    [Fact]
    public async Task GetApiKeyUsageDetail_ReturnsTotalsAndModelBreakdown()
    {
        await SeedKeyedAsync("k1", "alpha", "openai", "gpt-4o", promptTokens: 100, completionTokens: 50);
        await SeedKeyedAsync("k1", "alpha", "anthropic", "claude", promptTokens: 40, completionTokens: 20);
        await SeedKeyedAsync("k2", "beta", "openai", "gpt-4o"); // other key excluded

        var result = await CreateController().GetApiKeyUsageDetail("k1", WindowStart, WindowEnd, CancellationToken.None);

        var response = Assert.IsType<Unswarm.Api.Dtos.KeyUsageResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, response.Totals.RequestCount);
        Assert.Equal(140, response.Totals.PromptTokens);
        Assert.Equal(70, response.Totals.CompletionTokens);
        Assert.Equal(2, response.Models.Count);
    }

    [Fact]
    public async Task GetApiKeyUsageDetail_UnknownKey_ReturnsZeros()
    {
        await SeedKeyedAsync("k1", "alpha", "openai", "gpt-4o");

        var result = await CreateController().GetApiKeyUsageDetail("nope", WindowStart, WindowEnd, CancellationToken.None);

        var response = Assert.IsType<Unswarm.Api.Dtos.KeyUsageResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, response.Totals.RequestCount);
        Assert.Empty(response.Models);
    }

    // ─── Provider catalog ─────────────────────────────────────────────

    [Fact]
    public async Task GetProviderCatalog_UnionsSeenConfiguredAndRegistered()
    {
        // Seen in usage: local agent + cloud provider.
        await SeedAsync("runtime-a", "llama-3", agent: "agent-x");
        await SeedAsync("openai", "gpt-4o", providerKind: "cloud");

        // Configured cloud provider + registered runtime agent not yet seen.
        _db.CloudProviders.Add(new CloudProviderEntity
        {
            Id = "prov-1",
            Name = "anthropic",
            BaseUrl = "https://example.test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        _db.RegisteredRuntimes.Add(new RegisteredRuntimeEntity
        {
            Id = "rt-1",
            DisplayName = "runtime-b",
            Image = "img",
            Agent = "agent-y",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await CreateController().GetProviderCatalog(CancellationToken.None);

        var catalog = Assert.IsType<List<Unswarm.Api.Dtos.ProviderCatalogItem>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains(catalog, c => c.Name == "agent-x" && c.Kind == "agent");
        Assert.Contains(catalog, c => c.Name == "openai" && c.Kind == "cloud");
        Assert.Contains(catalog, c => c.Name == "anthropic" && c.Kind == "cloud");
        Assert.Contains(catalog, c => c.Name == "agent-y" && c.Kind == "agent");
        // Deduped by name.
        Assert.Equal(catalog.Select(c => c.Name).Distinct().Count(), catalog.Count);
    }

    [Fact]
    public async Task GetProviderCatalog_EmptyDatabase_ReturnsEmpty()
    {
        var result = await CreateController().GetProviderCatalog(CancellationToken.None);

        var catalog = Assert.IsType<List<Unswarm.Api.Dtos.ProviderCatalogItem>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(catalog);
    }

    // ─── Purge ────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeUsage_DeletesRecordsOlderThanWindow()
    {
        await SeedAsync("openai", "gpt-4o",
            timestamp: DateTimeOffset.UtcNow.AddDays(-40));
        await SeedAsync("openai", "gpt-4o",
            timestamp: DateTimeOffset.UtcNow.AddDays(-1));

        var result = await CreateController().PurgeUsage(olderThanDays: 30, ct: CancellationToken.None);

        var deleted = (int)Assert.IsType<OkObjectResult>(result).Value!
            .GetType().GetProperty("deleted")!.GetValue(Assert.IsType<OkObjectResult>(result).Value)!;
        Assert.Equal(1, deleted);
        Assert.Equal(1, await _db.UsageRecords.CountAsync());
    }

    [Fact]
    public async Task PurgeUsage_ZeroDays_DeletesEverything()
    {
        await SeedAsync("openai", "gpt-4o", timestamp: DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedAsync("openai", "gpt-4o", timestamp: DateTimeOffset.UtcNow.AddMinutes(-10));

        var result = await CreateController().PurgeUsage(olderThanDays: 0, ct: CancellationToken.None);

        var deleted = (int)Assert.IsType<OkObjectResult>(result).Value!
            .GetType().GetProperty("deleted")!.GetValue(Assert.IsType<OkObjectResult>(result).Value)!;
        Assert.Equal(2, deleted);
        Assert.Equal(0, await _db.UsageRecords.CountAsync());
    }

    [Fact]
    public async Task PurgeUsage_UsesSettingRetentionWhenNotOverridden()
    {
        var settings = new Settings { UsageRetentionDays = 7 };
        await SeedAsync("openai", "gpt-4o", timestamp: DateTimeOffset.UtcNow.AddDays(-30));
        await SeedAsync("openai", "gpt-4o", timestamp: DateTimeOffset.UtcNow.AddDays(-1));

        var controller = new MetricsController(_db, new FakeSettingsStore(settings), new NullLiveTailBroadcaster());
        var result = await controller.PurgeUsage(ct: CancellationToken.None);

        var deleted = (int)Assert.IsType<OkObjectResult>(result).Value!
            .GetType().GetProperty("deleted")!.GetValue(Assert.IsType<OkObjectResult>(result).Value)!;
        Assert.Equal(1, deleted);
    }

    // ─── Live tail WebSocket ──────────────────────────────────────────

    [Fact]
    public async Task LiveTail_NotAWebSocketRequest_Returns400()
    {
        var controller = new MetricsController(_db, new FakeSettingsStore(), new NullLiveTailBroadcaster());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        await controller.LiveTail(CancellationToken.None);

        Assert.Equal(400, controller.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task LiveTail_PushesPublishedEventsToSocket()
    {
        var broadcaster = new FakeUsageLiveTailBroadcasterCoverage1();
        broadcaster.Publish(new UsageLiveTailEvent(
            "evt-1", DateTimeOffset.UtcNow, "agent-x", "local", "llama-3", 10, 20, 0, false, 100, "agent-x"));
        broadcaster.Complete();

        var socket = new FakeWebSocket();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeatureCoverage1(socket));
        var controller = new MetricsController(_db, new FakeSettingsStore(), broadcaster);
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        await controller.LiveTail(CancellationToken.None);

        var sent = Assert.Single(socket.SentMessages);
        Assert.Contains("llama-3", sent);
        Assert.Contains("evt-1", sent);
    }

    [Fact]
    public async Task LiveTail_ClientCloseFrame_TerminatesStream()
    {
        var broadcaster = new FakeUsageLiveTailBroadcasterCoverage1();
        var socket = new FakeWebSocket();
        socket.EnqueueReceive(WebSocketMessageType.Close, []);
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeatureCoverage1(socket));
        var controller = new MetricsController(_db, new FakeSettingsStore(), broadcaster);
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        using var cts = new CancellationTokenSource();
        var streamTask = controller.LiveTail(cts.Token);
        await Eventually.UntilAsync(() => socket.CloseCallCount == 1);
        cts.Cancel();
        await streamTask;

        Assert.Equal(1, socket.CloseCallCount);
    }

    /// <summary>
    /// No-op live-tail broadcaster: the analytics endpoints only receive the
    /// dependency and never touch it.
    /// </summary>
    private sealed class NullLiveTailBroadcaster : IUsageLiveTailBroadcaster
    {
        public IUsageLiveTailSubscription Subscribe() =>
            new NullSubscription();

        public void Publish(UsageLiveTailEvent evt) { }

        private sealed class NullSubscription : IUsageLiveTailSubscription
        {
            public ChannelReader<UsageLiveTailEvent> Reader { get; } =
                Channel.CreateUnbounded<UsageLiveTailEvent>().Reader;

            public void Dispose() { }
        }
    }
}
