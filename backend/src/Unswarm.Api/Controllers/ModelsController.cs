using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Model registry — the catalog of discovered models and cloud provider models.
/// Models are auto-discovered from running inference containers; new models can
/// also be registered manually. Each model is linked to its source container/runtime.
/// </summary>
/// <remarks>
/// GET /api/models — List all models
/// POST /api/models — Register a new model
/// GET /api/models/{id} — Get a model detail
/// PUT /api/models/{id} — Update a model
/// DELETE /api/models/{id} — Delete a model
/// POST /api/models/test-chat — Send one admin test-chat turn through the proxy
/// </remarks>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class ModelsController : ControllerBase
{
    private readonly IModelRegistry _registry;
    private readonly IBenchmarkHistory _benchmarks;
    private readonly IContainerRegistry _containerRegistry;
    private readonly ICloudProviderStore _cloudProviderStore;
    private readonly ISchedulerQueue _scheduler;
    private readonly IClock _clock;
    private readonly ILogStore _logStore;
    private readonly ICloudForwardingService _cloudForwarding;
    private readonly IUsageRecorder _usageRecorder;

    public ModelsController(
        IModelRegistry registry,
        IBenchmarkHistory benchmarks,
        IContainerRegistry containerRegistry,
        ICloudProviderStore cloudProviderStore,
        ISchedulerQueue scheduler,
        IClock clock,
        ILogStore logStore,
        ICloudForwardingService cloudForwarding,
        IUsageRecorder usageRecorder)
    {
        _registry = registry;
        _benchmarks = benchmarks;
        _containerRegistry = containerRegistry;
        _cloudProviderStore = cloudProviderStore;
        _scheduler = scheduler;
        _clock = clock;
        _logStore = logStore;
        _cloudForwarding = cloudForwarding;
        _usageRecorder = usageRecorder;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var models = await _registry.ListAllAsync(ct);

        // Batch-load runtime names for models that have a source runtime
        var runtimeIds = models
            .Where(m => !string.IsNullOrEmpty(m.SourceRuntimeId))
            .Select(m => m.SourceRuntimeId!)
            .Distinct()
            .ToList();
        var runtimeInfoMap = new Dictionary<string, (string Name, string Agent)>();
        foreach (var rid in runtimeIds)
        {
            var rt = await _containerRegistry.GetAsync(rid, ct).ConfigureAwait(false);
            if (rt is not null)
                runtimeInfoMap[rid] = (string.IsNullOrEmpty(rt.DisplayName) ? rt.Image : rt.DisplayName, rt.Agent);
        }

        var responses = new List<ModelResponse>(models.Count);
        foreach (var model in models)
        {
            var last = await _benchmarks.GetLatestForModelAsync(model.Id, ct).ConfigureAwait(false);
            var response = ModelResponse.FromDefinition(model, last is null ? null : LastBenchmarkResponse.From(last));
            if (!string.IsNullOrEmpty(model.SourceRuntimeId) && runtimeInfoMap.TryGetValue(model.SourceRuntimeId, out var rtInfo))
            {
                response.SourceRuntimeName = rtInfo.Name;
                response.SourceRuntimeAgent = rtInfo.Agent;
            }
            responses.Add(response);
        }

        // Append cloud models from registered providers
        var providers = await _cloudProviderStore.ListAsync(ct);
        foreach (var provider in providers)
        {
            var modelIds = await _cloudProviderStore.GetModelIdsAsync(provider.Id, ct);
            foreach (var modelId in modelIds)
            {
                responses.Add(new ModelResponse
                {
                    Id = $"cloud/{provider.Name}/{modelId}",
                    Name = modelId,
                    Origin = "cloud",
                    ProviderName = provider.Name,
                    Status = ModelStatus.Ready,
                    CreatedAt = provider.CreatedAt,
                    UpdatedAt = provider.UpdatedAt
                });
            }
        }

        return Ok(responses);
    }

    [HttpGet("{*id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var model = await _registry.GetAsync(id, ct);
        if (model is null && !string.IsNullOrEmpty(id) && id[0] != '/')
            model = await _registry.GetAsync("/" + id, ct).ConfigureAwait(false);
        if (model is null) return NotFound();

        var last = await _benchmarks.GetLatestForModelAsync(model.Id, ct).ConfigureAwait(false);
        return Ok(ModelResponse.FromDefinition(model, last is null ? null : LastBenchmarkResponse.From(last)));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ModelCreateRequest request, CancellationToken ct)
    {
        var definition = new ModelDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Family = request.Family,
            ParameterSize = request.ParameterSize,
            Quantization = request.Quantization,
            ContextWindow = request.ContextWindow,
            ContainerImage = request.ContainerImage
        };

        var created = await _registry.CreateAsync(definition, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, ModelResponse.FromDefinition(created));
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("{*id}")]
    public async Task<IActionResult> Update(string id, [FromBody] ModelUpdateRequest request, CancellationToken ct)
    {
        var existing = await _registry.GetAsync(id, ct);
        if (existing is null && !string.IsNullOrEmpty(id) && id[0] != '/')
            existing = await _registry.GetAsync("/" + id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound();

        var updated = new ModelDefinition
        {
            Id = existing.Id,
            Name = request.Name ?? existing.Name,
            Family = request.Family ?? existing.Family,
            ParameterSize = request.ParameterSize ?? existing.ParameterSize,
            Quantization = request.Quantization ?? existing.Quantization,
            Status = request.Status ?? existing.Status,
            ContextWindow = request.ContextWindow ?? existing.ContextWindow,
            ContainerImage = request.ContainerImage ?? existing.ContainerImage,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt
        };

        var result = await _registry.UpdateAsync(existing.Id, updated, ct);
        return Ok(ModelResponse.FromDefinition(result));
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("{*id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var existing = await _registry.GetAsync(id, ct);
        if (existing is null && !string.IsNullOrEmpty(id) && id[0] != '/')
            existing = await _registry.GetAsync("/" + id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound();

        await _registry.DeleteAsync(existing.Id, ct);
        return NoContent();
    }

    // ── Test chat (interactive model/connection testing) ─────────────

    /// <summary>Hard ceiling for client-supplied max_tokens in test chat.</summary>
    private const int MaxTestChatTokens = 32768;

    /// <summary>
    /// Send one interactive test-chat turn to a model through the SAME pipeline as
    /// /v1/chat/completions: swarm models go through the scheduler queue, cloud
    /// models are forwarded directly to the provider. Streaming responses pass
    /// through untouched so the browser can render tokens live. Admin-only — this
    /// triggers real inference (and real cloud spend), mirroring benchmark runs.
    /// Cookie-authenticated by design: the dashboard cannot hold inference API keys.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPost("test-chat")]
    public async Task<IActionResult> TestChat([FromBody] TestChatRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Model))
            return BadRequest(new { error = "'model' is required" });

        var messages = new List<TestChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.System))
            messages.Add(new TestChatMessage { Role = "system", Content = request.System });
        // Note: "messages": null in JSON binds to a null property even though the
        // DTO initializes it — filter defensively.
        messages.AddRange((request.Messages ?? []).Where(m =>
            m is not null &&
            !string.IsNullOrWhiteSpace(m.Content) &&
            m.Role is "system" or "user" or "assistant"));

        if (messages.Count == 0)
            return BadRequest(new { error = "'messages' must contain at least one non-empty message" });

        if (request.Model.StartsWith("cloud/", StringComparison.Ordinal))
            return await TestChatCloudAsync(
                request.Model,
                messages, isStream: request.Stream,
                maxTokens: request.MaxTokens, temperature: request.Temperature, ct).ConfigureAwait(false);

        return await TestChatSwarmAsync(
            request.Model,
            messages, isStream: request.Stream,
            maxTokens: request.MaxTokens, temperature: request.Temperature, ct).ConfigureAwait(false);
    }

    /// <summary>Swarm path: enqueue on the scheduler queue exactly like /v1 traffic.</summary>
    private async Task<IActionResult> TestChatSwarmAsync(
        string modelId, List<TestChatMessage> messages, bool isStream, int? maxTokens, double? temperature, CancellationToken ct)
    {
        var model = await _registry.GetAsync(modelId, ct);
        if (model is null && modelId.Length > 0 && modelId[0] != '/')
            model = await _registry.GetAsync("/" + modelId, ct).ConfigureAwait(false);
        if (model is null) return NotFound(new { error = $"Model {modelId} not found" });

        // The engine-visible "model" field is the registry NAME — identical to what
        // external clients send to /v1/chat/completions (OpenAIController lists
        // models by name). Routing below uses the same value.
        var payloadJson = BuildTestChatPayload(model.Name, messages, isStream, maxTokens, temperature);

        _logStore.Enqueue(LogLevel.Info, "test-chat",
            $"Test chat start: model={model.Name}, stream={isStream}");

        var inferenceRequest = new InferenceRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = model.Name,
            OriginalJson = payloadJson,
            IsStreaming = isStream,
            Priority = 0,
            EnqueuedAt = _clock.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            CancellationToken = ct
        };

        var startTime = _clock.UtcNow;
        InferenceResponse response;
        try
        {
            response = await _scheduler.EnqueueAsync(inferenceRequest, ct);
        }
        catch (OperationCanceledException)
        {
            _logStore.Enqueue(LogLevel.Warn, "test-chat", $"Test chat cancelled: model={model.Name}");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logStore.Enqueue(LogLevel.Error, "test-chat",
                $"Test chat failed: model={model.Name}, error={ex.Message}");
            return StatusCode(502, new { error = ex.Message });
        }

        var elapsedMs = (_clock.UtcNow - startTime).TotalMilliseconds;
        _logStore.Enqueue(LogLevel.Info, "test-chat",
            $"Test chat complete: model={model.Name}, status={response.StatusCode}, duration={elapsedMs:F0}ms");

        WriteInferenceHeaders(response.StatusCode, response.ContentType, isStream);

        if (response.Body is not null)
        {
            try
            {
                await CopyResponseBodyAsync(response.Body, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Client disconnected mid-stream; headers are already sent — just end.
            }
            finally
            {
                // Always release upstream — disposal completes BodyDrained which the
                // scheduler awaits before freeing the slot (same rule as OpenAIController).
                await response.Body.DisposeAsync();
            }
        }

        _ = _usageRecorder.RecordAsync(
            response.ServedByRuntimeName ?? inferenceRequest.TargetId ?? "local",
            model.Name,
            response.PromptTokens,
            response.TokensGenerated,
            response.PromptTokensCached,
            isStream,
            elapsedMs,
            providerKind: "local");

        return new EmptyResult();
    }

    /// <summary>Cloud path: forward straight to the provider like cloud /v1 traffic.</summary>
    private async Task<IActionResult> TestChatCloudAsync(
        string modelName, List<TestChatMessage> messages, bool isStream, int? maxTokens, double? temperature, CancellationToken ct)
    {
        var payloadJson = BuildTestChatPayload(modelName, messages, isStream, maxTokens, temperature);

        _logStore.Enqueue(LogLevel.Info, "test-chat",
            $"Test chat start (cloud): model={modelName}, stream={isStream}");

        var startTime = _clock.UtcNow;
        CloudForwardResponse cloudResponse;
        try
        {
            cloudResponse = await _cloudForwarding.ForwardAsync(
                modelName, payloadJson, "/v1/chat/completions", isStream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logStore.Enqueue(LogLevel.Warn, "test-chat", $"Test chat cancelled (cloud): model={modelName}");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logStore.Enqueue(LogLevel.Error, "test-chat",
                $"Test chat failed (cloud): model={modelName}, error={ex.Message}");
            return StatusCode(502, new { error = ex.Message });
        }

        var elapsedMs = (_clock.UtcNow - startTime).TotalMilliseconds;
        _logStore.Enqueue(LogLevel.Info, "test-chat",
            $"Test chat complete (cloud): model={modelName}, status={cloudResponse.StatusCode}, duration={elapsedMs:F0}ms");

        WriteInferenceHeaders(cloudResponse.StatusCode, cloudResponse.ContentType, isStream);

        if (cloudResponse.Body is not null)
        {
            // Tap the stream so token counts land in usage metrics like /v1 does.
            // The tap writes its counts onto this response object at EOF/dispose.
            var tokenResponse = new InferenceResponse();
            var tapped = new StreamingTokenTapStream(cloudResponse.Body, tokenResponse);
            try
            {
                await CopyResponseBodyAsync(tapped, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Upstream closed early or client disconnected; nothing else to do.
            }
            finally
            {
                await tapped.DisposeAsync();
            }

            _ = _usageRecorder.RecordAsync(
                ExtractCloudProviderName(modelName) ?? "cloud",
                modelName,
                tokenResponse.PromptTokens,
                tokenResponse.TokensGenerated,
                tokenResponse.PromptTokensCached,
                isStream,
                elapsedMs,
                providerKind: "cloud");
        }

        return new EmptyResult();
    }

    /// <summary>Sets status/content-type (+ SSE cache headers when streaming).</summary>
    private void WriteInferenceHeaders(int statusCode, string contentType, bool isStream)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = contentType;
        if (isStream)
        {
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
        }
    }

    /// <summary>Piped body copy with per-chunk flushes for live streaming.</summary>
    private async Task CopyResponseBodyAsync(Stream source, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await Response.Body.WriteAsync(buffer, 0, bytesRead, ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Serializes an OpenAI chat-completions payload. The "model" field uses the
    /// registry name for swarm models (what external clients pass to /v1) and the
    /// full cloud/&lt;provider&gt;/&lt;model&gt; id otherwise.
    /// </summary>
    internal static string BuildTestChatPayload(
        string modelId,
        List<TestChatMessage> messages,
        bool isStream,
        int? maxTokens,
        double? temperature)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = isStream,
        };
        if (maxTokens is int mt)
            payload["max_tokens"] = Math.Clamp(mt, 1, MaxTestChatTokens);
        if (temperature is double t)
            payload["temperature"] = Math.Clamp(t, 0d, 2d);
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>"cloud/&lt;provider&gt;/&lt;model&gt;" → "&lt;provider&gt;", else null.</summary>
    private static string? ExtractCloudProviderName(string modelName)
    {
        if (!modelName.StartsWith("cloud/", StringComparison.Ordinal))
            return null;
        var rest = modelName["cloud/".Length..];
        var slashIdx = rest.IndexOf('/');
        return slashIdx > 0 ? rest[..slashIdx] : null;
    }
}
