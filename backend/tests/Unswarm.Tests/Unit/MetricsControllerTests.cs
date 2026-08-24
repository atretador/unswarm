using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Unswarm.Api.Controllers;
using Unswarm.Core.Contracts;
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
        bool streaming = false)
    {
        var ts = timestamp ?? new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        _db.UsageRecords.Add(new UsageRecordEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = ts,
            TimestampTicks = ts.UtcTicks,
            Provider = provider,
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
