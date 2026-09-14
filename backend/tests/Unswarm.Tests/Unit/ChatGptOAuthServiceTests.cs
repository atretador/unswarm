using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class ChatGptOAuthServiceTests
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly ChatGptOAuthService _service;

    public ChatGptOAuthServiceTests()
    {
        var factory = new FakeHttpClientFactory(_handler);
        var logger = new LoggerFactory().CreateLogger<ChatGptOAuthService>();
        _service = new ChatGptOAuthService(factory, logger);
    }

    // ── StartDeviceCodeFlowAsync ─────────────────────────────────────

    [Fact]
    public async Task StartDeviceCodeFlowAsync_ReturnsParsedFields()
    {
        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            device_auth_id = "dev-auth-123",
            user_code = "ABCD-1234",
            interval = 10
        }));

        var result = await _service.StartDeviceCodeFlowAsync(CancellationToken.None);

        Assert.Equal("dev-auth-123", result.DeviceAuthId);
        Assert.Equal("ABCD-1234", result.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", result.VerificationUrl);
        Assert.Equal(10, result.Interval);
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_DefaultInterval_WhenMissing()
    {
        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            device_auth_id = "dev-auth-456",
            user_code = "XYZW-5678"
        }));

        var result = await _service.StartDeviceCodeFlowAsync(CancellationToken.None);

        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_IntervalAsString_ParsedCorrectly()
    {
        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"device_auth_id":"d1","user_code":"U1","interval":"3"}""",
                Encoding.UTF8, "application/json")
        });

        var result = await _service.StartDeviceCodeFlowAsync(CancellationToken.None);

        Assert.Equal(3, result.Interval);
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_HttpError_Throws()
    {
        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _service.StartDeviceCodeFlowAsync(CancellationToken.None));
    }

    // ── PollForTokenAsync ────────────────────────────────────────────

    [Fact]
    public async Task PollForTokenAsync_404_ReturnsNull()
    {
        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await _service.PollForTokenAsync("auth-id", "USER-CODE", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PollForTokenAsync_403_ReturnsNull()
    {
        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _service.PollForTokenAsync("auth-id", "USER-CODE", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PollForTokenAsync_Success_ExchangeCodeForTokens()
    {
        // First response: poll returns authorization code + challenge + verifier
        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            authorization_code = "authz-code-abc",
            code_challenge = "challenge-xyz",
            code_verifier = "verifier-123"
        }));

        // Second response: token exchange succeeds
        // Build a JWT-like id_token with chatgpt_account_id claim
        var idToken = CreateJwtWithAccountId("acct-from-jwt");

        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            access_token = "at-new",
            refresh_token = "rt-new",
            expires_in = 3600,
            id_token = idToken
        }));

        var result = await _service.PollForTokenAsync("auth-id", "USER-CODE", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("at-new", result.AccessToken);
        Assert.Equal("rt-new", result.RefreshToken);
        Assert.Equal("acct-from-jwt", result.ChatgptAccountId);
        // ExpiresAt should be roughly now + 3600s
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.True(result.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(70));
    }

    [Fact]
    public async Task PollForTokenAsync_TokenExchangeFails_ThrowsInvalidOperation()
    {
        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            authorization_code = "authz-code",
            code_challenge = "challenge",
            code_verifier = "verifier"
        }));

        // Token exchange returns error status
        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid_grant", Encoding.UTF8, "text/plain")
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PollForTokenAsync("auth-id", "USER-CODE", CancellationToken.None));

        Assert.Contains("OAuth token exchange failed", ex.Message);
    }

    [Fact]
    public async Task PollForTokenAsync_TokenExchangeReturnsErrorJson_ThrowsInvalidOperation()
    {
        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            authorization_code = "authz-code",
            code_challenge = "challenge",
            code_verifier = "verifier"
        }));

        // Token exchange returns 200 but with error body
        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            error = "invalid_grant",
            error_description = "The authorization code has expired"
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PollForTokenAsync("auth-id", "USER-CODE", CancellationToken.None));

        Assert.Contains("The authorization code has expired", ex.Message);
    }

    // ── RefreshTokenAsync ────────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_Success_ReturnsOAuthTokenResult()
    {
        _handler.ResponseQueue.Enqueue(JsonResponse(new
        {
            access_token = "refreshed-at",
            refresh_token = "refreshed-rt",
            expires_in = 7200
        }));

        var result = await _service.RefreshTokenAsync("old-refresh-token", CancellationToken.None);

        Assert.Equal("refreshed-at", result.AccessToken);
        Assert.Equal("refreshed-rt", result.RefreshToken);
        Assert.Null(result.ChatgptAccountId); // no id_token, access token has no claim
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(100));
    }

    [Fact]
    public async Task RefreshTokenAsync_ErrorResponse_ThrowsInvalidOperation()
    {
        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                error = "invalid_grant",
                error_description = "Refresh token is expired"
            })
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RefreshTokenAsync("expired-refresh", CancellationToken.None));

        Assert.Contains("Refresh token is expired", ex.Message);
    }

    [Fact]
    public async Task RefreshTokenAsync_HttpError_Throws()
    {
        _handler.ResponseQueue.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error", Encoding.UTF8, "text/plain")
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _service.RefreshTokenAsync("old-refresh", CancellationToken.None));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static HttpResponseMessage JsonResponse(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// Creates a fake JWT with an OpenAI auth namespace claim containing chatgpt_account_id.
    /// </summary>
    private static string CreateJwtWithAccountId(string accountId)
    {
        // Header
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));

        // Payload with nested OpenAI auth namespace
        var payloadObj = new Dictionary<string, object>
        {
            ["sub"] = "user-123",
            ["https://api.openai.com/auth"] = new Dictionary<string, string>
            {
                ["chatgpt_account_id"] = accountId
            }
        };
        var payloadJson = JsonSerializer.Serialize(payloadObj);
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        // Signature (not verified by the service)
        var signature = Base64UrlEncode(Encoding.UTF8.GetBytes("fake-signature"));

        return $"{header}.{payload}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // ── Fakes ────────────────────────────────────────────────────────

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> ResponseQueue { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ResponseQueue.Count == 0)
                throw new InvalidOperationException("No fake response configured");

            return Task.FromResult(ResponseQueue.Dequeue());
        }
    }

    private sealed class FakeHttpClientFactory(FakeHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }
}
