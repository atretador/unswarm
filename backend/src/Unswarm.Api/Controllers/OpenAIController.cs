using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.Controllers;

[ApiController]
// Inference surface: an OpenAI-compatible proxy. ONLY managed inference API
// keys authenticate here — the InferenceKey policy rejects cookie principals
// (the ApiKeyAuthMiddleware fail-closed rule for /v1 admits either a valid
// inference key or an already-cookie-authenticated principal, but the policy
// below still requires the inference-scope claim, so cookie-only callers get
// 403). Use a generated key even for local testing.
[Authorize(Policy = "InferenceKey")]
[Route("v1")]
public sealed class OpenAIController : ControllerBase
{
    private readonly IModelRegistry _registry;
    private readonly ISchedulerQueue _scheduler;
    private readonly IClock _clock;
    private readonly ILogStore _logStore;
    private readonly ICloudForwardingService _cloudForwarding;
    private readonly ICloudProviderStore _cloudProviderStore;
    private readonly IUsageRecorder _usageRecorder;
    private readonly IApiKeyAccessService _apiKeyAccess;

    public OpenAIController(
        IModelRegistry registry,
        ISchedulerQueue scheduler,
        IClock clock,
        ILogStore logStore,
        ICloudForwardingService cloudForwarding,
        ICloudProviderStore cloudProviderStore,
        IUsageRecorder usageRecorder,
        IApiKeyAccessService apiKeyAccess)
    {
        _registry = registry;
        _scheduler = scheduler;
        _clock = clock;
        _logStore = logStore;
        _cloudForwarding = cloudForwarding;
        _cloudProviderStore = cloudProviderStore;
        _usageRecorder = usageRecorder;
        _apiKeyAccess = apiKeyAccess;
    }

    [HttpGet("models")]
    public async Task<IActionResult> ListModels(CancellationToken ct)
    {
        // Fleet models
        var models = await _registry.ListAllAsync(ct);
        var data = models.Select(m => new OpenAiModelData
        {
            Id = m.Name,
            Created = m.CreatedAt.ToUnixTimeSeconds(),
            Unswarm = new OpenAiModelUnswarmInfo
            {
                Family = m.Family,
                ParameterSize = m.ParameterSize,
                Quantization = m.Quantization,
                ContextWindow = m.ContextWindow,
                ContainerImage = m.ContainerImage,
                Status = m.Status.ToString().ToLowerInvariant()
            }
        }).ToList();

        // Cloud provider models
        var providers = await _cloudProviderStore.ListAsync(ct);
        foreach (var provider in providers)
        {
            var modelIds = await _cloudProviderStore.GetModelIdsAsync(provider.Id, ct);
            foreach (var modelId in modelIds)
            {
                data.Add(new OpenAiModelData
                {
                    Id = $"cloud/{provider.Name}/{modelId}",
                    Created = provider.CreatedAt.ToUnixTimeSeconds(),
                    OwnedBy = provider.Name,
                    Unswarm = new OpenAiModelUnswarmInfo() // empty defaults for cloud models
                });
            }
        }

        // Per-key model access control on the listing itself: a key with a
        // restricted KeyAccess must not discover model ids it cannot call.
        // Uses the same matching rules as IsModelAllowedAsync (via
        // FilterModelsAsync). Key-less callers (cookie-authenticated admin —
        // possible only if the policy is ever relaxed) see everything.
        string? apiKeyId = User.FindFirst("unswarm:key-id")?.Value;
        if (apiKeyId is not null)
        {
            var allowedIds = await _apiKeyAccess.FilterModelsAsync(
                apiKeyId, data.Select(d => d.Id), ct);
            var allowedSet = new HashSet<string>(allowedIds, StringComparer.OrdinalIgnoreCase);
            data = data.Where(d => allowedSet.Contains(d.Id)).ToList();
        }

        return Ok(new OpenAiModelListResponse { Data = data });
    }

    [HttpPost("chat/completions")]
    public async Task<IActionResult> ChatCompletions(CancellationToken ct)
    {
        return await HandleInferenceAsync(ct);
    }

    [HttpPost("completions")]
    public async Task<IActionResult> Completions(CancellationToken ct)
    {
        return await HandleInferenceAsync(ct);
    }

    private async Task<IActionResult> HandleInferenceAsync(CancellationToken ct)
    {
        // Usage attribution: ApiKeyAuthMiddleware stamps the managed key identity
        // for /v1 requests; cookie-authenticated admins carry no key id (null).
        string? apiKeyId = User.FindFirst("unswarm:key-id")?.Value;
        string? apiKeyName = apiKeyId is null ? null : User.FindFirst(ClaimTypes.Name)?.Value;

        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        string modelName;
        bool isStream;
        string? conversationKey;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            modelName = root.GetProperty("model").GetString() ?? "";
            isStream = root.TryGetProperty("stream", out var streamProp) && streamProp.GetBoolean();
            conversationKey = ExtractConversationKey(root, Request.Headers["X-Session-Id"].FirstOrDefault());
        }
        catch
        {
            return BadRequest(new { error = "Invalid JSON: 'model' field required" });
        }

        // Per-key model access control: enforced here (not in the middleware)
        // because only this controller parses the requested model id. Key-less
        // callers (cookie-authenticated admin) are unrestricted.
        if (apiKeyId is not null && !await _apiKeyAccess.IsModelAllowedAsync(apiKeyId, modelName, ct))
        {
            _logStore.Enqueue(LogLevel.Warn, "proxy",
                $"Access denied: key={apiKeyName ?? apiKeyId} requested model={modelName}");
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = new
                {
                    message = $"API key does not have access to model '{modelName}'.",
                    type = "invalid_request_error",
                    param = "model",
                    code = "model_access_denied"
                }
            });
        }

        // Cloud provider models bypass the local queue/scheduler entirely
        if (modelName.StartsWith("cloud/", StringComparison.Ordinal))
        {
            _logStore.Enqueue(LogLevel.Info, "cloud-proxy",
                $"Cloud request start: model={modelName}, stream={isStream}");

            var cloudStartTime = _clock.UtcNow;
            CloudForwardResponse cloudResponse;
            try
            {
                cloudResponse = await _cloudForwarding.ForwardAsync(
                    modelName, rawBody, Request.Path.Value ?? "/v1/chat/completions", isStream, ct);
            }
            catch (OperationCanceledException)
            {
                _logStore.Enqueue(LogLevel.Warn, "cloud-proxy",
                    $"Cloud request cancelled: model={modelName}");
                return StatusCode(499);
            }
            catch (Exception ex)
            {
                _logStore.Enqueue(LogLevel.Error, "cloud-proxy",
                    $"Cloud request failed: model={modelName}, error={ex.Message}");
                return StatusCode(502, new { error = "Cloud inference request failed" });
            }

            var cloudElapsedMs = (long)(_clock.UtcNow - cloudStartTime).TotalMilliseconds;
            _logStore.Enqueue(LogLevel.Info, "cloud-proxy",
                $"Cloud request complete: model={modelName}, status={cloudResponse.StatusCode}, duration={cloudElapsedMs}ms");

            Response.StatusCode = cloudResponse.StatusCode;
            Response.ContentType = cloudResponse.ContentType;

            if (isStream)
            {
                Response.Headers["Cache-Control"] = "no-cache";
                Response.Headers["X-Accel-Buffering"] = "no";
            }

            if (cloudResponse.Body is not null)
            {
                var cloudTokenResponse = new InferenceResponse();
                var tappedStream = new StreamingTokenTapStream(cloudResponse.Body, cloudTokenResponse);
                try
                {
                    var buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = await tappedStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await Response.Body.WriteAsync(buffer, 0, bytesRead, ct);
                        await Response.Body.FlushAsync(ct);
                    }
                }
                catch (IOException ex)
                {
                    // Upstream closed prematurely or client disconnected during
                    // write. Since HTTP 200 + headers are already sent, the stream
                    // just ends — log and let finally handle upstream disposal.
                    _logStore.Enqueue(LogLevel.Warn, "cloud-proxy",
                        $"Cloud stream interrupted: model={modelName}, error={ex.Message}");
                }
                finally
                {
                    await tappedStream.DisposeAsync();
                }

                _ = _usageRecorder.RecordAsync(
                    ExtractCloudProviderName(modelName) ?? "cloud",
                    modelName,
                    cloudTokenResponse.PromptTokens,
                    cloudTokenResponse.TokensGenerated,
                    cloudTokenResponse.PromptTokensCached,
                    isStream,
                    cloudElapsedMs,
                    apiKeyId,
                    apiKeyName,
                    providerKind: "cloud");
            }

            return new EmptyResult();
        }

        _logStore.Enqueue(LogLevel.Info, "proxy",
            $"Request start: model={modelName}, stream={isStream}");

        var request = new InferenceRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = modelName,
            OriginalJson = rawBody,
            IsStreaming = isStream,
            Priority = 0,
            EnqueuedAt = _clock.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            CancellationToken = ct,
            ConversationKey = conversationKey
        };

        var startTime = _clock.UtcNow;
        InferenceResponse inferenceResponse;
        try
        {
            inferenceResponse = await _scheduler.EnqueueAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            _logStore.Enqueue(LogLevel.Warn, "proxy",
                $"Request cancelled: model={modelName}");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logStore.Enqueue(LogLevel.Error, "proxy",
                $"Request failed: model={modelName}, error={ex.Message}");
            return StatusCode(502, new { error = "Inference request failed" });
        }

        var elapsedMs = (long)(_clock.UtcNow - startTime).TotalMilliseconds;
        _logStore.Enqueue(LogLevel.Info, "proxy",
            $"Request complete: model={modelName}, status={inferenceResponse.StatusCode}, " +
            $"tokens={inferenceResponse.TokensGenerated}, duration={elapsedMs}ms");

        Response.StatusCode = inferenceResponse.StatusCode;

        if (isStream)
        {
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            Response.ContentType = "text/event-stream";
        }
        else
        {
            Response.ContentType = inferenceResponse.ContentType;
        }

        if (inferenceResponse.Body is not null)
        {
            try
            {
                var buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await inferenceResponse.Body.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await Response.Body.WriteAsync(buffer, 0, bytesRead, ct);
                    await Response.Body.FlushAsync(ct);
                }
            }
            catch (IOException ex)
            {
                // Upstream closed prematurely or client disconnected during
                // write. Since HTTP 200 + headers are already sent, the stream
                // just ends — log and let finally handle upstream disposal.
                _logStore.Enqueue(LogLevel.Warn, "proxy",
                    $"Stream interrupted: model={modelName}, error={ex.Message}");
            }
            finally
            {
                // Always release the upstream body — including on client
                // disconnect (OperationCanceledException) or write failure.
                // Disposing completes BodyDrained, which the scheduler awaits
                // before freeing the target slot; without this a cancelled
                // mid-stream request leaves the queue stuck forever.
                await inferenceResponse.Body.DisposeAsync();
            }
        }

        _ = _usageRecorder.RecordAsync(
            inferenceResponse.ServedByRuntimeName ?? request.TargetId ?? "local",
            modelName,
            inferenceResponse.PromptTokens,
            inferenceResponse.TokensGenerated,
            inferenceResponse.PromptTokensCached,
            isStream,
            elapsedMs,
            apiKeyId,
            apiKeyName,
            providerKind: "local");

        return new EmptyResult();
    }

    /// <summary>
    /// Extracts the concrete cloud provider name from a "cloud/&lt;provider&gt;/&lt;model&gt;"
    /// model id — the same segment CloudForwardingService resolves via
    /// GetByNameAsync. Returns null for malformed ids (caller falls back to "cloud").
    /// </summary>
    private static string? ExtractCloudProviderName(string modelName)
    {
        if (!modelName.StartsWith("cloud/", StringComparison.Ordinal))
            return null;

        var rest = modelName["cloud/".Length..];
        var slashIdx = rest.IndexOf('/');
        return slashIdx > 0 ? rest[..slashIdx] : null;
    }

    /// <summary>
    /// Fingerprints a chat-completions request into a stable conversation key used
    /// by the scheduler's affinity hold. Precedence: non-empty OpenAI "user" body
    /// field, then non-empty X-Session-Id header (both → "sid:&lt;value&gt;").
    /// Otherwise a SHA256 fingerprint over the first up-to-2 messages (each
    /// contributing role + "\n" + content truncated to 2048 chars), hex-prefixed
    /// "conv:" — stable across tool-call-loop iterations because harnesses resend
    /// the full history. No messages → null (no affinity).
    /// </summary>
    private static string? ExtractConversationKey(JsonElement root, string? sessionIdHeader)
    {
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.String)
        {
            var userId = user.GetString();
            if (!string.IsNullOrWhiteSpace(userId))
                return "sid:" + userId;
        }

        if (!string.IsNullOrWhiteSpace(sessionIdHeader))
            return "sid:" + sessionIdHeader;

        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        var count = Math.Min(2, messages.GetArrayLength());
        if (count == 0)
            return null;

        using var buffer = new MemoryStream();
        for (var i = 0; i < count; i++)
        {
            var message = messages[i];
            var role = message.TryGetProperty("role", out var roleProp) && roleProp.ValueKind == JsonValueKind.String
                ? roleProp.GetString() ?? ""
                : "";
            var content = message.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String
                ? contentProp.GetString() ?? ""
                : "";
            if (content.Length > 2048)
                content = content[..2048];

            var bytes = System.Text.Encoding.UTF8.GetBytes(role + "\n" + content);
            buffer.Write(bytes, 0, bytes.Length);
        }

        var hash = System.Security.Cryptography.SHA256.HashData(buffer.ToArray());
        return "conv:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
