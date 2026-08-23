using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Core.Services;

/// <summary>
/// Unqueued cloud LLM forwarding service. Proxies requests to cloud providers,
/// bypassing the local scheduler entirely. Uses a global concurrency cap to
/// prevent runaway parallel upstream connections.
/// </summary>
public sealed class CloudForwardingService : ICloudForwardingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogStore _logStore;
    private readonly IClock _clock;
    private readonly ILogger<CloudForwardingService> _logger;
    private readonly SemaphoreSlim _concurrencyCap = new(8, 8);

    public CloudForwardingService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogStore logStore,
        IClock clock,
        ILogger<CloudForwardingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logStore = logStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CloudForwardResponse> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct)
    {
        var startTime = _clock.UtcNow;

        // ── 1. Parse model id ──────────────────────────────────────────
        // Format: cloud/<providerName>/<rest-of-model>
        var rest = modelId["cloud/".Length..]; // skip "cloud/"
        var slashIdx = rest.IndexOf('/');
        if (slashIdx < 0)
        {
            _logStore.Enqueue(LogLevel.Warn, "cloud-proxy",
                $"Invalid cloud model id: {modelId}");
            return new CloudForwardResponse
            {
                StatusCode = 400,
                ContentType = "application/json",
                Body = ToErrorStream(400, $"Invalid cloud model id: {modelId}")
            };
        }

        var providerName = rest[..slashIdx];
        var upstreamModel = rest[(slashIdx + 1)..];

        // ── 2. Resolve provider via scoped store ───────────────────────
        string providerBaseUrl;
        string providerId;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICloudProviderStore>();
            var provider = await store.GetByNameAsync(providerName, ct);
            if (provider is null)
            {
                _logStore.Enqueue(LogLevel.Warn, "cloud-proxy",
                    $"Cloud provider '{providerName}' not found for model {modelId}");
                return new CloudForwardResponse
                {
                    StatusCode = 404,
                    ContentType = "application/json",
                    Body = ToErrorStream(404, $"Cloud provider '{providerName}' not found")
                };
            }

            providerBaseUrl = provider.BaseUrlFull;
            providerId = provider.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve cloud provider {Provider}", providerName);
            _logStore.Enqueue(LogLevel.Error, "cloud-proxy",
                $"Failed to resolve cloud provider '{providerName}': {ex.Message}");
            return new CloudForwardResponse
            {
                StatusCode = 500,
                ContentType = "application/json",
                Body = ToErrorStream(500, "Internal error resolving cloud provider")
            };
        }

        // ── 3. Decrypt API key ─────────────────────────────────────────
        string apiKey;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICloudProviderStore>();
            var decrypted = await store.GetApiKeyAsync(providerId, ct);
            if (string.IsNullOrEmpty(decrypted))
            {
                _logStore.Enqueue(LogLevel.Error, "cloud-proxy",
                    $"API key for provider '{providerName}' is empty or could not be decrypted");
                return new CloudForwardResponse
                {
                    StatusCode = 500,
                    ContentType = "application/json",
                    Body = ToErrorStream(500, $"Failed to decrypt API key for provider '{providerName}'")
                };
            }
            apiKey = decrypted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt API key for provider {Provider}", providerName);
            _logStore.Enqueue(LogLevel.Error, "cloud-proxy",
                $"Failed to decrypt API key for provider '{providerName}': {ex.Message}");
            return new CloudForwardResponse
            {
                StatusCode = 500,
                ContentType = "application/json",
                Body = ToErrorStream(500, $"Failed to decrypt API key for provider '{providerName}'")
            };
        }

        // ── 4. Concurrency cap ─────────────────────────────────────────
        if (!await _concurrencyCap.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            _logStore.Enqueue(LogLevel.Warn, "cloud-proxy",
                $"Cloud provider concurrency limit exceeded for {providerName}");
            return new CloudForwardResponse
            {
                StatusCode = 503,
                ContentType = "application/json",
                Body = ToErrorStream(503, "Cloud provider concurrency limit exceeded")
            };
        }

        try
        {
            // ── 5. Rewrite model field in request body ──────────────────
            string rewrittenBody;
            try
            {
                rewrittenBody = RewriteModelField(requestBody, upstreamModel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rewrite model field in request body for {Provider}", providerName);
                // Forward the original body if rewriting fails — the upstream
                // provider will reject an unknown model, but we should not block.
                rewrittenBody = requestBody;
            }

            // ── 6. Build upstream request ───────────────────────────────
            var upstreamUrl = providerBaseUrl.TrimEnd('/') + requestPath;

            var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
            {
                Content = new StringContent(rewrittenBody, Encoding.UTF8, "application/json")
            };
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // ── 7. Send and relay response ──────────────────────────────
            var client = _httpClientFactory.CreateClient("cloud-provider");
            using var upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var statusCode = (int)upstreamResponse.StatusCode;
            var contentType = upstreamResponse.Content.Headers.ContentType?.MediaType
                              ?? "application/json";

            var elapsedMs = (long)(_clock.UtcNow - startTime).TotalMilliseconds;
            _logStore.Enqueue(LogLevel.Info, "cloud-proxy",
                $"Cloud request complete: provider={providerName}, model={upstreamModel}, " +
                $"status={statusCode}, stream={isStreaming}, duration={elapsedMs}ms");

            if (isStreaming)
            {
                // For streaming: return the raw response stream. The caller
                // (controller) is responsible for piping and disposing.
                var stream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new CloudForwardResponse
                {
                    StatusCode = statusCode,
                    ContentType = contentType,
                    Body = stream
                };
            }

            // Non-streaming: buffer the full response into a MemoryStream
            // so we can dispose the HttpResponseMessage properly.
            var bodyStream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffered = new MemoryStream();
            await bodyStream.CopyToAsync(buffered, ct).ConfigureAwait(false);
            buffered.Position = 0;
            await bodyStream.DisposeAsync().ConfigureAwait(false);

            return new CloudForwardResponse
            {
                StatusCode = statusCode,
                ContentType = contentType,
                Body = buffered
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — no error body needed.
            _logStore.Enqueue(LogLevel.Warn, "cloud-proxy",
                $"Cloud request cancelled: provider={providerName}, model={upstreamModel}");
            return new CloudForwardResponse { StatusCode = 499, ContentType = "application/json" };
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Upstream timeout (HttpClient timeout or cancellation not from caller).
            _logger.LogWarning(ex, "Cloud provider request timed out: {Provider}/{Model}",
                providerName, upstreamModel);
            _logStore.Enqueue(LogLevel.Warn, "cloud-proxy",
                $"Cloud request timed out: provider={providerName}, model={upstreamModel}");
            return new CloudForwardResponse
            {
                StatusCode = 504,
                ContentType = "application/json",
                Body = ToErrorStream(504, "Cloud provider request timed out")
            };
        }
        catch (HttpRequestException ex)
        {
            // Connection / TLS failure.
            _logger.LogError(ex, "Cloud provider connection failed: {Provider}/{Model}",
                providerName, upstreamModel);
            _logStore.Enqueue(LogLevel.Error, "cloud-proxy",
                $"Cloud connection failed: provider={providerName}, model={upstreamModel}, error={ex.Message}");
            return new CloudForwardResponse
            {
                StatusCode = 502,
                ContentType = "application/json",
                Body = ToErrorStream(502, "Cloud provider connection failed")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error forwarding to cloud provider {Provider}/{Model}",
                providerName, upstreamModel);
            _logStore.Enqueue(LogLevel.Error, "cloud-proxy",
                $"Cloud forward error: provider={providerName}, model={upstreamModel}, error={ex.Message}");
            return new CloudForwardResponse
            {
                StatusCode = 500,
                ContentType = "application/json",
                Body = ToErrorStream(500, "Internal error forwarding to cloud provider")
            };
        }
        finally
        {
            _concurrencyCap.Release();
        }
    }

    /// <summary>
    /// Rewrites the "model" field in a JSON request body to the upstream model id.
    /// Uses <see cref="JsonNode"/> for mutable DOM access (no allocation of full document).
    /// </summary>
    private static string RewriteModelField(string requestBody, string upstreamModel)
    {
        var node = JsonNode.Parse(requestBody);
        if (node is null)
            return requestBody;

        if (node["model"] is not null)
        {
            node["model"] = upstreamModel;
        }

        return node.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// Creates a <see cref="MemoryStream"/> containing an OpenAI-compatible error JSON body.
    /// Stream position is reset to 0 for immediate reading.
    /// </summary>
    private static MemoryStream ToErrorStream(int status, string message)
    {
        var errorJson = JsonSerializer.Serialize(new
        {
            error = new
            {
                message,
                type = status switch
                {
                    404 => "not_found",
                    502 => "upstream_error",
                    503 => "upstream_error",
                    504 => "upstream_timeout",
                    _ => "error"
                },
                code = status
            }
        });

        var stream = new MemoryStream(Encoding.UTF8.GetBytes(errorJson));
        stream.Position = 0;
        return stream;
    }
}
