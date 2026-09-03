using System.Buffers;
using System.IO.Pipelines;
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
/// Forwards chat/completions requests to the ChatGPT subscription (Codex Responses API),
/// translating between the two wire protocols. OAuth tokens are refreshed proactively
/// when within 5 minutes of expiry. The upstream SSE stream is translated on-the-fly
/// to chat/completions format for the caller.
/// </summary>
public sealed class ChatGPTSubscriptionForwardingService : IChatGPTSubscriptionForwardingService
{
    private readonly IApiKeyEncryptor _encryptor;
    private readonly IChatGptOAuthService _oauthService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogStore _logStore;
    private readonly IClock _clock;
    private readonly ILogger<ChatGPTSubscriptionForwardingService> _logger;
    private readonly SemaphoreSlim _concurrencyCap = new(8, 8);

    // Responses API endpoint
    private const string ResponsesApiUrl = "https://chatgpt.com/backend-api/codex/responses";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChatGPTSubscriptionForwardingService(
        IApiKeyEncryptor encryptor,
        IChatGptOAuthService oauthService,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogStore logStore,
        IClock clock,
        ILogger<ChatGPTSubscriptionForwardingService> logger)
    {
        _encryptor = encryptor;
        _oauthService = oauthService;
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logStore = logStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Stream> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct)
    {
        var startTime = _clock.UtcNow;

        // ── 1. Parse model id ──────────────────────────────────────────
        // Format: cloud/<providerName>/<upstreamModel>
        var rest = modelId["cloud/".Length..];
        var slashIdx = rest.IndexOf('/');
        if (slashIdx < 0)
        {
            _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                $"Invalid subscription model id: {modelId}");
            return ToErrorStream(400, $"Invalid subscription model id: {modelId}");
        }

        var providerName = rest[..slashIdx];
        var upstreamModel = rest[(slashIdx + 1)..];

        // ── 2. Resolve provider via scoped store ───────────────────────
        string providerId;
        int authType;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICloudProviderStore>();
            var provider = await store.GetByNameAsync(providerName, ct);
            if (provider is null)
            {
                _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                    $"Cloud provider '{providerName}' not found for model {modelId}");
                return ToErrorStream(404, $"Cloud provider '{providerName}' not found");
            }

            providerId = provider.Id;
            authType = await store.GetAuthTypeAsync(providerId, ct);
            if (authType != 1) // CloudProviderAuthType.ChatGPTSubscription
            {
                _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                    $"Provider '{providerName}' is not a ChatGPT subscription provider (authType={authType})");
                return ToErrorStream(400, $"Provider '{providerName}' is not a ChatGPT subscription provider");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve cloud provider {Provider}", providerName);
            _logStore.Enqueue(LogLevel.Error, "chatgpt-sub",
                $"Failed to resolve cloud provider '{providerName}': {ex.Message}");
            return ToErrorStream(500, "Internal error resolving cloud provider");
        }

        // ── 3. Get OAuth tokens and refresh if needed ──────────────────
        string accessToken;
        string chatgptAccountId;
        try
        {
            var tokenResult = await GetOrRefreshAccessTokenAsync(providerId, providerName, ct);
            accessToken = tokenResult.accessToken;
            chatgptAccountId = tokenResult.chatgptAccountId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain OAuth token for provider {Provider}", providerName);
            _logStore.Enqueue(LogLevel.Error, "chatgpt-sub",
                $"Failed to obtain OAuth token for provider '{providerName}': {ex.Message}");
            return ToErrorStream(500, $"Failed to obtain OAuth token for provider '{providerName}'");
        }

        // ── 4. Concurrency cap ─────────────────────────────────────────
        if (!await _concurrencyCap.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                $"Subscription provider concurrency limit exceeded for {providerName}");
            return ToErrorStream(503, "Subscription provider concurrency limit exceeded");
        }

        HttpResponseMessage? upstreamResponse = null;
        try
        {
            // ── 5. Translate request body ──────────────────────────────
            string translatedBody;
            try
            {
                translatedBody = TranslateRequest(requestBody, upstreamModel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to translate request body for {Provider}", providerName);
                _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                    $"Failed to translate request body for '{providerName}': {ex.Message}");
                return ToErrorStream(400, "Failed to translate request body");
            }

            // ── 6. Build upstream request ──────────────────────────────
            var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, ResponsesApiUrl)
            {
                Content = new StringContent(translatedBody, Encoding.UTF8, "application/json")
            };

            // Required headers for Codex Responses API
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            upstreamRequest.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", chatgptAccountId);
            upstreamRequest.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
            upstreamRequest.Headers.TryAddWithoutValidation("originator", "unswarm");

            // ── 7. Send and relay response ─────────────────────────────
            var client = _httpClientFactory.CreateClient("cloud-provider");
            upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var statusCode = (int)upstreamResponse.StatusCode;
            var contentType = upstreamResponse.Content.Headers.ContentType?.MediaType
                              ?? "text/event-stream";

            var elapsedMs = (long)(_clock.UtcNow - startTime).TotalMilliseconds;
            _logStore.Enqueue(LogLevel.Info, "chatgpt-sub",
                $"Subscription request complete: provider={providerName}, model={upstreamModel}, " +
                $"status={statusCode}, duration={elapsedMs}ms");

            if (statusCode >= 400)
            {
                // Read error body and return as-is (not SSE)
                try
                {
                    var errorBody = await upstreamResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                        $"Upstream error {statusCode} from provider '{providerName}': {errorBody[..Math.Min(200, errorBody.Length)]}");
                }
                catch { /* best effort */ }

                var errorStream = ToErrorStream(statusCode, $"Upstream returned {statusCode}");
                upstreamResponse.Dispose();
                return errorStream;
            }

            // ── 8. Streaming: wrap in SSE translation stream ───────────
            var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ResponsesApiSseTranslationStream(
                new ResponseOwningStream(upstreamStream, upstreamResponse));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            upstreamResponse?.Dispose();
            _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                $"Subscription request cancelled: provider={providerName}, model={upstreamModel}");
            return Stream.Null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            upstreamResponse?.Dispose();
            _logger.LogWarning(ex, "Subscription provider request timed out: {Provider}/{Model}",
                providerName, upstreamModel);
            _logStore.Enqueue(LogLevel.Warn, "chatgpt-sub",
                $"Subscription request timed out: provider={providerName}, model={upstreamModel}");
            return ToErrorStream(504, "Subscription provider request timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Subscription provider connection failed: {Provider}/{Model}",
                providerName, upstreamModel);
            _logStore.Enqueue(LogLevel.Error, "chatgpt-sub",
                $"Subscription connection failed: provider={providerName}, model={upstreamModel}, error={ex.Message}");
            return ToErrorStream(502, "Subscription provider connection failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error forwarding to subscription provider {Provider}/{Model}",
                providerName, upstreamModel);
            _logStore.Enqueue(LogLevel.Error, "chatgpt-sub",
                $"Subscription forward error: provider={providerName}, model={upstreamModel}, error={ex.Message}");
            return ToErrorStream(500, "Internal error forwarding to subscription provider");
        }
        finally
        {
            _concurrencyCap.Release();
        }
    }

    // ── OAuth token management ────────────────────────────────────────────

    private async Task<(string accessToken, string chatgptAccountId)> GetOrRefreshAccessTokenAsync(
        string providerId, string providerName, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICloudProviderStore>();
        var tokens = await store.GetOAuthTokensAsync(providerId, ct)
            ?? throw new InvalidOperationException($"No OAuth tokens for provider '{providerName}'");

        var accessToken = _encryptor.Unprotect(tokens.AccessTokenCiphertext);
        var chatgptAccountId = tokens.ChatgptAccountId ?? string.Empty;

        // Check if token is expired or expiring within 5 minutes
        if (tokens.ExpiresAt.HasValue && tokens.ExpiresAt.Value > _clock.UtcNow.AddMinutes(5))
        {
            return (accessToken, chatgptAccountId);
        }

        // Token expiring soon — refresh proactively
        _logger.LogInformation("Refreshing OAuth token for provider {Provider} (expires {ExpiresAt})",
            providerName, tokens.ExpiresAt);

        var refreshToken = _encryptor.Unprotect(tokens.RefreshTokenCiphertext);
        var refreshed = await _oauthService.RefreshTokenAsync(refreshToken, ct);

        // Persist the new tokens
        var newAccessCipher = _encryptor.Protect(refreshed.AccessToken);
        var newRefreshCipher = _encryptor.Protect(refreshed.RefreshToken);
        var resolvedAccountId = refreshed.ChatgptAccountId ?? chatgptAccountId;

        await store.SaveOAuthTokensAsync(
            providerId,
            newAccessCipher,
            newRefreshCipher,
            refreshed.ExpiresAt,
            resolvedAccountId,
            ct);

        _logger.LogInformation("Refreshed OAuth token for provider {Provider}", providerName);
        _logStore.Enqueue(LogLevel.Info, "chatgpt-sub",
            $"Refreshed OAuth token for provider '{providerName}'");

        return (refreshed.AccessToken, resolvedAccountId);
    }

    // ── Request translation ───────────────────────────────────────────────

    /// <summary>
    /// Translates a chat/completions request body into the Responses API format.
    /// </summary>
    internal static string TranslateRequest(string requestBody, string upstreamModel)
    {
        var node = JsonNode.Parse(requestBody)
            ?? throw new ArgumentException("Invalid JSON request body");

        var result = new JsonObject
        {
            ["model"] = upstreamModel,
            ["stream"] = true,
            ["store"] = false
        };

        // ── Extract system messages → instructions ─────────────────────
        var messages = node["messages"]?.AsArray();
        if (messages is null || messages.Count == 0)
            throw new ArgumentException("Request body has no messages array");

        var instructions = new List<string>();
        var input = new JsonArray();

        foreach (var msg in messages)
        {
            if (msg is null) continue;
            var role = msg["role"]?.GetValue<string>() ?? "";

            if (role == "system")
            {
                // Extract text content for instructions
                var content = ExtractTextContent(msg["content"]);
                if (!string.IsNullOrEmpty(content))
                    instructions.Add(content);
                continue;
            }

            // Non-system messages → input[] items
            var inputItem = new JsonObject
            {
                ["type"] = "message",
                ["role"] = role
            };

            var contentNode = msg["content"];
            if (contentNode is JsonArray contentArray)
            {
                // Multimodal content array
                var translatedContent = new JsonArray();
                foreach (var part in contentArray)
                {
                    if (part is null) continue;
                    var partType = part["type"]?.GetValue<string>() ?? "";
                    switch (partType)
                    {
                        case "text":
                            translatedContent.Add(new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = part["text"]?.GetValue<string>() ?? ""
                            });
                            break;
                        case "image_url":
                            var imageUrl = part["image_url"]?["url"]?.GetValue<string>() ?? "";
                            translatedContent.Add(new JsonObject
                            {
                                ["type"] = "input_image",
                                ["image_url"] = imageUrl
                            });
                            break;
                        // Ignore unknown content types
                    }
                }
                inputItem["content"] = translatedContent;
            }
            else if (contentNode is JsonValue contentValue)
            {
                // Simple string content
                var text = contentValue.GetValue<string>() ?? "";
                inputItem["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = text
                    }
                };
            }

            input.Add(inputItem);
        }

        if (instructions.Count > 0)
            result["instructions"] = string.Join("\n\n", instructions);

        result["input"] = input;

        // ── Copy supported top-level fields ────────────────────────────
        // Tools and tool_choice pass through
        if (node["tools"] is not null)
            result["tools"] = node["tools"]?.DeepClone();

        if (node["tool_choice"] is not null)
            result["tool_choice"] = node["tool_choice"]?.DeepClone();
        else
            result["tool_choice"] = "auto";

        if (node["parallel_tool_calls"] is not null)
            result["parallel_tool_calls"] = node["parallel_tool_calls"]?.DeepClone();
        else
            result["parallel_tool_calls"] = true;

        // Reasoning pass-through (Responses API supports it)
        if (node["reasoning"] is not null)
            result["reasoning"] = node["reasoning"]?.DeepClone();

        return result.ToJsonString(JsonOpts);
    }

    /// <summary>
    /// Extracts text from either a string value or a content array (picks the first text part).
    /// </summary>
    private static string ExtractTextContent(JsonNode? content)
    {
        if (content is JsonValue sv)
            return sv.GetValue<string>() ?? "";

        if (content is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var part in arr)
            {
                if (part is null) continue;
                if (part["type"]?.GetValue<string>() == "text")
                {
                    var text = part["text"]?.GetValue<string>();
                    if (text is not null)
                        parts.Add(text);
                }
            }
            return string.Join("\n", parts);
        }

        return "";
    }

    // ── Error stream helper ───────────────────────────────────────────────

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

// ═══════════════════════════════════════════════════════════════════════════
// SSE Translation Stream
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Stream wrapper that reads Responses API SSE from upstream, translates each event
/// into chat/completions SSE format, and serves the translated bytes to the caller.
/// Uses Pipe for backpressure: a background task reads upstream and writes translated
/// events into the PipeWriter; the caller reads from the PipeReader.
/// </summary>
internal sealed class ResponsesApiSseTranslationStream : Stream
{
    private readonly Stream _upstream;
    private readonly Pipe _pipe;
    private readonly Task _pumpTask;
    private bool _disposed;

    public ResponsesApiSseTranslationStream(Stream upstream)
    {
        _upstream = upstream;
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 65536,
            resumeWriterThreshold: 32768,
            minimumSegmentSize: 4096,
            useSynchronizationContext: false));

        _pumpTask = Task.Run(() => PumpUpstreamAsync(_pipe.Writer));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Synchronous bridge — read from PipeReader into the caller's buffer
        var task = _pipe.Reader.ReadAsync();
        if (task.IsCompleted)
        {
            var result = task.Result;
            var data = result.Buffer;
            var toCopy = (int)Math.Min(data.Length, count);
            data.Slice(0, toCopy).CopyTo(buffer.AsSpan(offset));
            _pipe.Reader.AdvanceTo(data.GetPosition(toCopy));
            return toCopy;
        }

        // Must go async
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var result = await _pipe.Reader.ReadAsync(ct).ConfigureAwait(false);
        var data = result.Buffer;
        var toCopy = (int)Math.Min(data.Length, count);
        data.Slice(0, toCopy).CopyTo(buffer.AsSpan(offset));
        _pipe.Reader.AdvanceTo(data.GetPosition(toCopy));
        return toCopy;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var result = await _pipe.Reader.ReadAsync(ct).ConfigureAwait(false);
        var data = result.Buffer;
        var toCopy = (int)Math.Min(data.Length, buffer.Length);
        data.Slice(0, toCopy).CopyTo(buffer.Span);
        _pipe.Reader.AdvanceTo(data.GetPosition(toCopy));
        return toCopy;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _pipe.Reader.Complete();
            _pipe.Writer.Complete();
            _upstream.Dispose();
            _pumpTask.ContinueWith(_ => { }); // avoid unobserved exception
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe.Reader.Complete();
        _pipe.Writer.Complete();
        await _upstream.DisposeAsync().ConfigureAwait(false);
        try { await _pumpTask.ConfigureAwait(false); } catch { /* already completed */ }
    }

    // ── Upstream pump: read Responses API SSE → translate → write to pipe ─

    private async Task PumpUpstreamAsync(PipeWriter writer)
    {
        const int ReadBufSize = 8192;
        var readBuf = new byte[ReadBufSize];
        var encoding = Encoding.UTF8;

        // Partial-line accumulator across reads
        var lineBuilder = new StringBuilder();
        // Current SSE event type from "event: <type>" line
        string? currentEventType = null;

        // Tool call state tracking
        string? currentToolCallId = null;
        string? currentToolCallName = null;
        int toolCallIndex = 0;

        try
        {
            while (true)
            {
                var bytesRead = await _upstream.ReadAsync(readBuf, 0, ReadBufSize).ConfigureAwait(false);
                if (bytesRead == 0) break; // upstream EOF

                var chunk = encoding.GetString(readBuf, 0, bytesRead);
                int searchStart = 0;

                while (searchStart < chunk.Length)
                {
                    var nlIndex = chunk.IndexOf('\n', searchStart);
                    if (nlIndex < 0)
                    {
                        // Incomplete line — buffer the remainder
                        lineBuilder.Append(chunk.AsSpan(searchStart));
                        break;
                    }

                    // Complete line (including the trailing \n)
                    var line = chunk.Substring(searchStart, nlIndex - searchStart).TrimEnd('\r');
                    searchStart = nlIndex + 1;

                    // ── Process the complete line ──────────────────────
                    if (string.IsNullOrEmpty(line))
                    {
                        // Empty line = end of SSE event boundary
                        currentEventType = null;
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.Ordinal))
                    {
                        currentEventType = line["event:".Length..].Trim();
                        continue;
                    }

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    var json = line["data:".Length..].TrimStart();

                    if (string.IsNullOrEmpty(json) || json == "[DONE]")
                    {
                        // Flush any accumulated tool call state
                        if (currentToolCallId is not null)
                        {
                            await WriteTranslatedEventAsync(writer,
                                $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{toolCallIndex},\"id\":\"{currentToolCallId}\",\"type\":\"function\",\"function\":{{\"name\":\"\",\"arguments\":\"\"}}}}]}},\"finish_reason\":null}}]}}\n\n").ConfigureAwait(false);
                            currentToolCallId = null;
                            currentToolCallName = null;
                        }

                        if (json == "[DONE]")
                        {
                            await WriteStringAsync(writer, "data: [DONE]\n\n").ConfigureAwait(false);
                        }

                        currentEventType = null;
                        continue;
                    }

                    // Process the JSON event
                    var translated = TranslateEvent(json, currentEventType ?? "", ref currentToolCallId, ref currentToolCallName, ref toolCallIndex);
                    if (translated is not null)
                    {
                        await WriteTranslatedEventAsync(writer, translated).ConfigureAwait(false);
                    }
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }

            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log but don't crash — pipe reader will see EOF
            try
            {
                var errorEvent = $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"error\":{{\"message\":\"{EscapeJson(ex.Message)}\",\"type\":\"upstream_error\"}}}}\n\ndata: [DONE]\n\n";
                await WriteStringAsync(writer, errorEvent).ConfigureAwait(false);
            }
            catch { /* pipe may already be completed */ }
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Translates a Responses API SSE event JSON into chat/completions SSE format.
    /// Returns null if the event should be ignored.
    /// </summary>
    private static string? TranslateEvent(
        string json,
        string eventType,
        ref string? currentToolCallId,
        ref string? currentToolCallName,
        ref int toolCallIndex)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Determine event type from the JSON if not provided via event: line
            var type = eventType;
            if (string.IsNullOrEmpty(type) && root.TryGetProperty("type", out var typeProp))
            {
                type = typeProp.GetString() ?? "";
            }

            switch (type)
            {
                case "response.created":
                case "response.in_progress":
                case "response.content_part.added":
                case "response.output_text.done":
                case "response.content_part.done":
                    // Ignore
                    return null;

                case "response.output_item.added":
                {
                    if (!root.TryGetProperty("item", out var item)) return null;
                    var itemType = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

                    if (itemType == "message")
                    {
                        var role = item.TryGetProperty("role", out var r) ? r.GetString() ?? "assistant" : "assistant";
                        if (role == "assistant")
                        {
                            return $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"{role}\",\"content\":\"\"}},\"finish_reason\":null}}]}}\n\n";
                        }
                        return null;
                    }

                    if (itemType == "function_call" || itemType == "tool_call")
                    {
                        // Tool call start — we'll emit on the first arguments delta
                        currentToolCallId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        currentToolCallName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                        return null;
                    }

                    return null;
                }

                case "response.output_text.delta":
                {
                    var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(delta)) return null;
                    return $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{EscapeJson(delta)}\"}},\"finish_reason\":null}}]}}\n\n";
                }

                case "response.function_call_arguments.delta":
                case "response.function_call_arguments.done":
                {
                    // Accumulate or emit tool call arguments
                    var argumentsDelta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(argumentsDelta) && type.Contains(".done"))
                        return null; // done with no delta — ignore
                    if (string.IsNullOrEmpty(argumentsDelta))
                        return null;

                    if (currentToolCallId is not null)
                    {
                        return $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{toolCallIndex},\"function\":{{\"arguments\":\"{EscapeJson(argumentsDelta)}\"}}}}]}},\"finish_reason\":null}}]}}\n\n";
                    }

                    // Standalone tool call arguments without prior output_item.added
                    currentToolCallId = "call_" + Guid.NewGuid().ToString("N")[..16];
                    currentToolCallName = "";
                    return $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{toolCallIndex},\"id\":\"{currentToolCallId}\",\"type\":\"function\",\"function\":{{\"name\":\"{EscapeJson(currentToolCallName)}\",\"arguments\":\"{EscapeJson(argumentsDelta)}\"}}}}]}},\"finish_reason\":null}}]}}\n\n";
                }

                case "response.output_item.done":
                {
                    var item = root.TryGetProperty("item", out var itemProp) ? itemProp : default;
                    var itemType = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var t)
                        ? t.GetString() ?? "" : "";

                    if (itemType is "function_call" or "tool_call")
                    {
                        // Emit tool call end
                        if (currentToolCallId is not null)
                        {
                            var result = $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{toolCallIndex},\"id\":\"{currentToolCallId}\",\"type\":\"function\",\"function\":{{\"name\":\"{EscapeJson(currentToolCallName ?? "")}\",\"arguments\":\"\"}}}}]}},\"finish_reason\":null}}]}}\n\n";
                            currentToolCallId = null;
                            currentToolCallName = null;
                            toolCallIndex++;
                            return result;
                        }
                        return null;
                    }

                    if (itemType == "message")
                    {
                        // Text message done — emit stop if not already tool calling
                        if (currentToolCallId is null)
                        {
                            return $"{{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}\n\n";
                        }
                        return null;
                    }

                    return null;
                }

                case "response.completed":
                {
                    // Extract usage if present
                    var usageStr = "";
                    if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("usage", out var usage))
                    {
                        usageStr = usage.GetRawText();
                    }
                    else if (root.TryGetProperty("usage", out var usageDirect))
                    {
                        usageStr = usageDirect.GetRawText();
                    }

                    if (!string.IsNullOrEmpty(usageStr))
                    {
                        return $"{{\"choices\":[],\"usage\":{usageStr}}}\n\ndata: [DONE]\n\n";
                    }
                    return "data: [DONE]\n\n";
                }

                case "response.failed":
                case "response.incomplete":
                {
                    var error = root.TryGetProperty("response", out var resp2) && resp2.TryGetProperty("error", out var err)
                        ? err.GetRawText()
                        : "{\"message\":\"Unknown upstream error\",\"type\":\"upstream_error\"}";
                    return $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"error\":{error}}}\n\ndata: [DONE]\n\n";
                }

                default:
                    // Unknown event — ignore
                    return null;
            }
        }
        catch
        {
            // Malformed JSON — ignore the event
            return null;
        }
    }

    private static async Task WriteTranslatedEventAsync(PipeWriter writer, string sseEvent)
    {
        var bytes = Encoding.UTF8.GetBytes(sseEvent);
        var result = await writer.WriteAsync(bytes).ConfigureAwait(false);
        if (result.IsCanceled || result.IsCompleted)
            throw new IOException("Pipe writer completed or cancelled");
    }

    private static async Task WriteStringAsync(PipeWriter writer, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        await writer.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}

/// <summary>
/// Stream decorator that owns both the upstream content stream and the
/// HttpResponseMessage that produced it. Disposing the wrapper releases both.
/// </summary>
file sealed class ResponseOwningStream(Stream inner, HttpResponseMessage response) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => inner.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => inner.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        response.Dispose();
    }
}
