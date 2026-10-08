using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for <see cref="ChatGPTSubscriptionForwardingService"/> and the internal
/// Responses-API SSE translation stream it returns: request translation, OAuth
/// resolve/refresh, upstream SSE→chat-completions translation, and every error
/// branch (invalid id, unknown / non-subscription provider, token failures,
/// upstream 4xx/5xx, timeout, transport failure, cancellation, unexpected error).
/// </summary>
public sealed class ChatGPTSubscriptionForwardingServiceTests
{
    private readonly FakeCloudProviderStoreCoverage3 _store = new();
    private readonly FakeHttpClientFactoryCoverage3 _http = new();
    private readonly FakeOAuthCoverage3 _oauth = new();
    private readonly FakeEncryptorCoverage3 _encryptor = new();
    private readonly FakeLogStore _logs = new();
    private readonly FakeClock _clock = new();

    private const string RequestBody = """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""";

    private (ChatGPTSubscriptionForwardingService Service, ServiceProvider Provider) Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICloudProviderStore>(_store);
        var provider = services.BuildServiceProvider();
        var service = new ChatGPTSubscriptionForwardingService(
            _encryptor,
            _oauth,
            _http,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _logs,
            _clock,
            new LoggerFactory().CreateLogger<ChatGPTSubscriptionForwardingService>());
        return (service, provider);
    }

    private static OAuthTokenSet Tokens(DateTimeOffset expiresAt)
        => new("enc:access", "enc:refresh", expiresAt, "acct-1");

    private static HttpResponseMessage Sse(string body, int status = 200)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    // ── Happy path: SSE translation ────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_TranslatesResponsesSseToChatCompletions()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));
        _http.Handler = _ => Sse(
            "event: response.output_item.added\n" +
            "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"message\",\"role\":\"assistant\"}}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\n" +
            "event: response.output_item.added\n" +
            "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"id\":\"call_1\",\"name\":\"do_thing\"}}\n\n" +
            "event: response.function_call_arguments.delta\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{\\\"x\\\":1}\"}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\"}}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"message\"}}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}}\n\n" +
            "data: [DONE]\n\n");

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", isStreaming: true, CancellationToken.None);

            var output = await ReadAllAsync(stream);

            Assert.Contains("\"content\":\"Hello\"", output);
            Assert.Contains("tool_calls", output);
            Assert.Contains("\"finish_reason\":\"stop\"", output);
            Assert.Contains("[DONE]", output);
        }

        // Upstream request went to the Codex Responses endpoint with the OAuth token.
        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("access", request.Headers.Authorization.Parameter);
        Assert.Contains("\"model\":\"gpt-5\"", await request.Content!.ReadAsStringAsync());
    }

    // ── Validation / resolution errors ─────────────────────────────────

    [Fact]
    public async Task ForwardAsync_InvalidModelId_Returns400Json()
    {
        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("Invalid subscription model id", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_UnknownProvider_Returns404Json()
    {
        _store.Seed("prov-1", "other", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("not found", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_NonSubscriptionProvider_Returns400Json()
    {
        _store.Seed("prov-1", "chatgpt", authType: 0, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("not a ChatGPT subscription provider", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_ProviderResolveThrows_Returns500Json()
    {
        _store.GetByNameException = new InvalidOperationException("store down");

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("Internal error resolving cloud provider", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_MissingOAuthTokens_Returns500Json()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: null);

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("Failed to obtain OAuth token", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_InvalidRequestBody_Returns400Json()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", "{not valid json", "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("Failed to translate request body", await ReadAllAsync(stream));
        }
    }

    // ── Token refresh ──────────────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_ExpiringToken_RefreshesAndPersists()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(_clock.UtcNow.AddMinutes(1)));
        _oauth.RefreshResult = new OAuthTokenResult("fresh-access", "fresh-refresh", _clock.UtcNow.AddHours(1), "acct-2");
        _http.Handler = _ => Sse("event: response.completed\ndata: {\"type\":\"response.completed\"}\n\ndata: [DONE]\n\n");

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", true, CancellationToken.None);
            await ReadAllAsync(stream);
        }

        var saved = Assert.Single(_store.SavedTokens);
        Assert.Equal("enc:fresh-access", saved.Access);
        Assert.Equal("enc:fresh-refresh", saved.Refresh);
        Assert.Equal("acct-2", saved.AccountId);
        Assert.Equal("fresh-access", Assert.Single(_http.Requests).Headers.Authorization!.Parameter);
        Assert.True(_oauth.RefreshCalled);
    }

    // ── Upstream failures ──────────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_UpstreamErrorStatus_ReturnsErrorJson()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));
        _http.Handler = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"rate limited\"}", Encoding.UTF8, "application/json")
        };

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("Upstream returned 429", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_TransportFailure_Returns502Json()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));
        _http.HandlerAsync = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("boom"));

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("connection failed", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_Timeout_Returns504Json()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));
        _http.HandlerAsync = (_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"));

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("timed out", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_UnexpectedError_Returns500Json()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));
        _http.HandlerAsync = (_, _) => Task.FromException<HttpResponseMessage>(new InvalidOperationException("kaboom"));

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, CancellationToken.None);

            Assert.Contains("Internal error forwarding", await ReadAllAsync(stream));
        }
    }

    [Fact]
    public async Task ForwardAsync_CancelledDuringSend_ReturnsNullStream()
    {
        _store.Seed("prov-1", "chatgpt", authType: 1, tokens: Tokens(DateTimeOffset.UtcNow.AddHours(1)));
        using var cts = new CancellationTokenSource();
        _http.HandlerAsync = (_, _) =>
        {
            cts.Cancel(); // simulate client disconnect mid-send
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException());
        };

        var (service, provider) = Create();
        await using (provider)
        {
            using var stream = await service.ForwardAsync(
                "cloud/chatgpt/gpt-5", RequestBody, "/v1/chat/completions", false, cts.Token);

            Assert.Same(Stream.Null, stream);
        }
    }

    // ── Fakes ──────────────────────────────────────────────────────────

    private sealed class FakeEncryptorCoverage3 : IApiKeyEncryptor
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string ciphertext) => ciphertext.StartsWith("enc:", StringComparison.Ordinal)
            ? ciphertext["enc:".Length..]
            : ciphertext;
    }

    private sealed class FakeOAuthCoverage3 : IChatGptOAuthService
    {
        public OAuthTokenResult? RefreshResult { get; set; }
        public bool RefreshCalled { get; private set; }

        public Task<DeviceCodeResult> StartDeviceCodeFlowAsync(CancellationToken ct)
            => Task.FromResult(new DeviceCodeResult("d", "u", "v", 5));

        public Task<OAuthTokenResult?> PollForTokenAsync(string deviceAuthId, string userCode, CancellationToken ct)
            => Task.FromResult<OAuthTokenResult?>(null);

        public Task<OAuthTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken ct)
        {
            RefreshCalled = true;
            return Task.FromResult(RefreshResult ?? new OAuthTokenResult("r-access", "r-refresh", DateTimeOffset.UtcNow.AddHours(1), "acct"));
        }
    }
}
