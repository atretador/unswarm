using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class CloudForwardingServiceTests : IAsyncLifetime
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly FakeCloudProviderStore _providerStore = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeClock _clock = new();
    private readonly CloudForwardingService _service;

    public CloudForwardingServiceTests()
    {
        var httpClientFactory = new FakeHttpClientFactory(_handler);
        var scopeFactory = new FakeServiceScopeFactory(_providerStore);
        var logger = new LoggerFactory().CreateLogger<CloudForwardingService>();
        _service = new CloudForwardingService(httpClientFactory, scopeFactory, _logStore, _clock, logger);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    // ── 1. Invalid cloud model id ────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_InvalidModelId_Returns400()
    {
        // "cloud/noSlash" has no second slash after "cloud/"
        var response = await _service.ForwardAsync(
            "cloud/openai", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Invalid cloud model id", await ReadBody(response));
    }

    // ── 2. Provider not found ────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_ProviderNotFound_Returns404()
    {
        // "cloud/unknown/gpt-4o" → providerName = "unknown" (not in store)
        var response = await _service.ForwardAsync(
            "cloud/unknown/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("not found", await ReadBody(response));
    }

    // ── 3. API key empty ─────────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_EmptyApiKey_Returns500()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");
        _providerStore.ApiKeyOverride = ""; // empty

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("decrypt API key", await ReadBody(response));
    }

    // ── 4. Successful non-streaming request ──────────────────────────

    [Fact]
    public async Task ForwardAsync_NonStreaming_Success()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"chatcmpl-123","choices":[{"message":{"role":"assistant","content":"Hi!"}}]}""",
                Encoding.UTF8, "application/json")
        });

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o",
            """{"model":"cloud/openai/gpt-4o","messages":[{"role":"user","content":"Hello"}]}""",
            "/v1/chat/completions",
            false,
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);

        var body = await ReadBody(response);
        Assert.Contains("chatcmpl-123", body);
        Assert.Contains("Hi!", body);
    }

    // ── 5. Successful streaming request ──────────────────────────────

    [Fact]
    public async Task ForwardAsync_Streaming_ReturnsReadableBody()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        var streamContent = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n",
            Encoding.UTF8, "text/event-stream");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = streamContent
        });

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o",
            """{"model":"cloud/openai/gpt-4o"}""",
            "/v1/chat/completions",
            true,
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.Body);
        Assert.True(response.Body!.CanRead);
    }

    // ── 6. Model field rewrite ───────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_RewritesModelField()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });

        await _service.ForwardAsync(
            "cloud/openai/gpt-4o",
            """{"model":"cloud/openai/gpt-4o","messages":[]}""",
            "/v1/chat/completions",
            false,
            CancellationToken.None);

        // Verify the request body sent to upstream was rewritten
        Assert.Single(_handler.Requests);
        var sentBody = await _handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains(""""model":"gpt-4o"""", sentBody);
        Assert.DoesNotContain("cloud/", sentBody);
    }

    // ── 7. Upstream HTTP error ───────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_UpstreamError_RelayedStatusCode()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"rate limited"}""", Encoding.UTF8, "application/json")
        });

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Equal(429, response.StatusCode);
        Assert.Contains("rate limited", await ReadBody(response));
    }

    // ── 8. Upstream timeout (TaskCanceledException not from caller) ──

    [Fact]
    public async Task ForwardAsync_UpstreamTimeout_Returns504()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ExceptionToThrow = new TaskCanceledException("timeout from httpclient");

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Equal(504, response.StatusCode);
        Assert.Contains("timed out", await ReadBody(response));
    }

    // ── 9. Connection failure (HttpRequestException) ─────────────────

    [Fact]
    public async Task ForwardAsync_ConnectionFailure_Returns502()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ExceptionToThrow = new HttpRequestException("Connection refused");

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Equal(502, response.StatusCode);
        Assert.Contains("connection failed", await ReadBody(response));
    }

    // ── 10. Cancellation from caller → 499 ───────────────────────────

    [Fact]
    public async Task ForwardAsync_CallerCancellation_Returns499()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        // Handler simulates the caller cancelling mid-request
        _handler.SendHandler = async (req, ct) =>
        {
            cts.Cancel(); // cancel before returning
            throw new OperationCanceledException(token);
        };

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, token);

        Assert.Equal(499, response.StatusCode);
    }

    // ── 11. Provider resolution throws → 500 ────────────────────────

    [Fact]
    public async Task ForwardAsync_ProviderResolutionThrows_Returns500()
    {
        _providerStore.GetByNameThrows = true;

        var response = await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("Internal error", await ReadBody(response));
    }

    // ── URL construction ─────────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_CorrectUpstreamUrl()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });

        await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Single(_handler.Requests);
        Assert.Equal(new Uri("https://api.openai.com/v1/chat/completions"), _handler.Requests[0].RequestUri);
    }

    [Fact]
    public async Task ForwardAsync_StripDoubleV1()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });

        await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        // The /v1 prefix is stripped from requestPath before appending to base URL
        Assert.Equal(new Uri("https://api.openai.com/v1/chat/completions"), _handler.Requests[0].RequestUri);
    }

    // ── Forwarded headers ────────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_ForwardsHeaders()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });

        var headers = new Dictionary<string, string>
        {
            ["X-Custom-Header"] = "custom-value",
            ["X-Trace-Id"] = "trace-123"
        };

        await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None, headers);

        Assert.Single(_handler.Requests);
        Assert.True(_handler.Requests[0].Headers.Contains("X-Custom-Header"));
        Assert.Equal("custom-value", _handler.Requests[0].Headers.GetValues("X-Custom-Header").First());
        Assert.True(_handler.Requests[0].Headers.Contains("X-Trace-Id"));
    }

    // ── Auth header ──────────────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_SetsAuthorizationHeader()
    {
        _providerStore.SeedProvider("cp-1", "openai", "https://api.openai.com/v1");

        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        });

        await _service.ForwardAsync(
            "cloud/openai/gpt-4o", "{}", "/v1/chat/completions", false, CancellationToken.None);

        Assert.Single(_handler.Requests);
        Assert.True(_handler.Requests[0].Headers.Authorization != null);
        Assert.Equal("Bearer", _handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("sk-decrypted-test-key", _handler.Requests[0].Headers.Authorization!.Parameter);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<string> ReadBody(CloudForwardResponse response)
    {
        if (response.Body is null) return string.Empty;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    // ── Fakes ────────────────────────────────────────────────────────

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> ResponseQueue { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = [];
        public Exception? ExceptionToThrow { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendHandler { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (SendHandler is not null)
                return await SendHandler(request, cancellationToken);

            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            if (ResponseQueue.Count == 0)
                throw new InvalidOperationException("No fake response configured");

            return ResponseQueue.Dequeue();
        }
    }

    private sealed class FakeHttpClientFactory(FakeHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private sealed class FakeCloudProviderStore : ICloudProviderStore
    {
        private readonly Dictionary<string, CloudProviderReadItem> _items = new();
        private int _nextId = 1;

        public string? ApiKeyOverride { get; set; }
        public bool GetByNameThrows { get; set; }

        public void SeedProvider(string id, string name, string baseUrl)
        {
            _items[id] = new CloudProviderReadItem
            {
                Id = id,
                Name = name,
                BaseUrl = baseUrl,
                BaseUrlFull = baseUrl,
                ApiKeyHint = "sk-…test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        public Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task CreateAsync(string name, string baseUrl, string? apiKeyPlaintext, string apiKeyHint, int authType, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudProviderListItem>>([]);

        public Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default)
        {
            _items.TryGetValue(id, out var item);
            return Task.FromResult(item);
        }

        public Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default)
        {
            if (GetByNameThrows) throw new InvalidOperationException("Simulated store failure");

            var item = _items.Values.FirstOrDefault(i => i.Name == name);
            return Task.FromResult(item);
        }

        public Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_items.Values.Any(i => i.Name == name));

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_items.Remove(id));

        public Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default)
            => Task.FromResult(ApiKeyOverride ?? "sk-decrypted-test-key");

        public Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task SaveOAuthTokensAsync(string id, string accessTokenCiphertext, string refreshTokenCiphertext, DateTimeOffset? expiresAt, string? chatgptAccountId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<OAuthTokenSet?> GetOAuthTokensAsync(string id, CancellationToken ct = default)
            => Task.FromResult<OAuthTokenSet?>(null);

        public Task<int> GetAuthTypeAsync(string id, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class FakeServiceScopeFactory(FakeCloudProviderStore store) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FakeScope(store);
    }

    private sealed class FakeScope(FakeCloudProviderStore store) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new FakeServiceProvider(store);
        public void Dispose() { }
    }

    private sealed class FakeServiceProvider(FakeCloudProviderStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ICloudProviderStore))
                return store;
            return null;
        }
    }

    private sealed class FakeLogStore : ILogStore
    {
        public List<(Unswarm.Core.Models.LogLevel Level, string Source, string Message)> Entries { get; } = [];

        public void Enqueue(Unswarm.Core.Models.LogLevel level, string source, string message, Dictionary<string, object>? metadata = null)
            => Entries.Add((level, source, message));

        public Task<IReadOnlyList<Unswarm.Core.Models.LogEntry>> GetHistoricalAsync(
            string? source = null,
            Unswarm.Core.Models.LogLevel? level = null,
            int limit = 100,
            DateTimeOffset? since = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Unswarm.Core.Models.LogEntry>>([]);

        public async IAsyncEnumerable<Unswarm.Core.Models.LogEntry> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
