using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Benchmarks;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Benchmark history and model performance evaluation. Run benchmark prompts
/// against models (fleet or cloud) to measure tokens/sec, latency, and response quality.
/// </summary>
/// <remarks>
/// POST /api/benchmarks?modelId= — Run a benchmark (Admin only)
/// GET /api/benchmarks — List benchmark history
/// GET /api/benchmarks?modelId= — Filter benchmarks by model
/// </remarks>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class BenchmarksController : ControllerBase
{
    private readonly IModelRegistry _registry;
    private readonly ISchedulerQueue _scheduler;
    private readonly IClock _clock;
    private readonly IBenchmarkHistory _history;
    private readonly IPromptStore _prompts;
    private readonly ICloudForwardingService _cloudForwarding;

    // Shared built-in prompt + max-token cap live in BenchmarkDefaults (Core) so the
    // automatic post-registration benchmark uses the exact same values.
    private const int MaxTokens = BenchmarkDefaults.MaxTokens;

    /// <summary>Maximum characters retained in benchmark history response/reasoning fields.</summary>
    private const int MaxStoredResponseChars = BenchmarkDefaults.MaxStoredResponseChars;

    public BenchmarksController(
        IModelRegistry registry,
        ISchedulerQueue scheduler,
        IClock clock,
        IBenchmarkHistory history,
        IPromptStore prompts,
        ICloudForwardingService cloudForwarding)
    {
        _registry = registry;
        _scheduler = scheduler;
        _clock = clock;
        _history = history;
        _prompts = prompts;
        _cloudForwarding = cloudForwarding;
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Run([FromQuery] string modelId, [FromBody] BenchmarkRunRequest? body, CancellationToken ct)
    {
        // ── Cloud model path ──────────────────────────────────────────────
        // Cloud IDs (cloud/<provider>/<model>) are not in the fleet registry;
        // forward the request directly to the upstream provider via
        // CloudForwardingService and record the result in benchmark history.
        if (modelId.StartsWith("cloud/", StringComparison.Ordinal))
        {
            return await RunCloudBenchmarkAsync(modelId, body, ct);
        }

        // ── Fleet model path (existing) ───────────────────────────────────
        var model = await _registry.GetAsync(modelId, ct);
        if (model is null) return NotFound(new { error = $"Model {modelId} not found" });

        var resolved = await ResolvePromptAsync(body, ct);
        if (resolved.ErrorResult is not null) return resolved.ErrorResult;

        var requestJson = BenchmarkDefaults.BuildChatPayload(model.Id, resolved.Prompt, resolved.MaxTokens);

        var request = new InferenceRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = model.Name,
            OriginalJson = requestJson,
            IsStreaming = false,
            Priority = 0,
            EnqueuedAt = _clock.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            CancellationToken = ct
        };

        var sw = Stopwatch.StartNew();
        InferenceResponse response;
        try
        {
            response = await _scheduler.EnqueueAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Benchmark cancelled" });
        }
        catch (Exception ex)
        {
            // Persist the failure so the history is honest.
            var failedEntry = await _history.AddAsync(
                model.Id,
                resolved.Prompt,
                tokensPerSec: 0,
                latencyMs: sw.Elapsed.TotalMilliseconds,
                tokensGenerated: 0,
                status: "error",
                errorMessage: ex.Message,
                ct,
                resolved.PromptId,
                resolved.PromptName,
                resolved.PromptVersion).ConfigureAwait(false);

            var failedResponse = BenchmarkResponse.FromEntry(failedEntry);
            failedResponse.ModelName = model.Name;
            return StatusCode(502, failedResponse);
        }

        sw.Stop();
        var elapsedMs = sw.Elapsed.TotalMilliseconds;

        var tokensGenerated = response.TokensGenerated;
        var tokensPerSec = response.ServerTokensPerSec > 0
            ? response.ServerTokensPerSec
            : elapsedMs > 0 && tokensGenerated > 0
                ? tokensGenerated / (elapsedMs / 1000.0)
                : 0;

        // Capture the model's answer text AND reasoning text for history.
        // Best-effort: nulls on any read/parse failure, never throws. Thinking
        // models (e.g. Qwen3.x) put all generated text in reasoning_content, so
        // both parts must be captured to avoid empty history rows.
        var responseParts = await BenchmarkDefaults.ExtractResponsePartsAsync(response.Body, ct).ConfigureAwait(false);

        var entry = await _history.AddAsync(
            model.Id,
            resolved.Prompt,
            tokensPerSec,
            elapsedMs,
            tokensGenerated,
            status: "completed",
            errorMessage: null,
            ct,
            resolved.PromptId,
            resolved.PromptName,
            resolved.PromptVersion,
            responseParts.Content,
            responseParts.Reasoning).ConfigureAwait(false);

        var responseItem = BenchmarkResponse.FromEntry(entry);
        responseItem.ModelName = model.Name;
        return Ok(responseItem);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Run a benchmark against a cloud model (IDs starting with <c>cloud/</c>).
    /// The request is forwarded directly to the upstream provider; no scheduler
    /// involvement. The model name for display is extracted from the ID:
    /// <c>cloud/&lt;provider&gt;/&lt;model&gt;</c> → <c>provider/model</c>.
    /// </summary>
    private async Task<IActionResult> RunCloudBenchmarkAsync(
        string modelId, BenchmarkRunRequest? body, CancellationToken ct)
    {
        var resolved = await ResolvePromptAsync(body, ct);
        if (resolved.ErrorResult is not null) return resolved.ErrorResult;

        var payload = BenchmarkDefaults.BuildChatPayload(modelId, resolved.Prompt, resolved.MaxTokens);

        var sw = Stopwatch.StartNew();
        Core.Contracts.CloudForwardResponse cloudResponse;
        try
        {
            cloudResponse = await _cloudForwarding
                .ForwardAsync(modelId, payload, "/v1/chat/completions", isStreaming: false, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499, new { error = "Benchmark cancelled" });
        }
        catch (Exception ex)
        {
            sw.Stop();
            var failedEntry = await _history.AddAsync(
                modelId,
                resolved.Prompt,
                tokensPerSec: 0,
                latencyMs: sw.Elapsed.TotalMilliseconds,
                tokensGenerated: 0,
                status: "error",
                errorMessage: ex.Message,
                ct,
                resolved.PromptId,
                resolved.PromptName,
                resolved.PromptVersion).ConfigureAwait(false);

            var failedResponse = BenchmarkResponse.FromEntry(failedEntry);
            failedResponse.ModelName = FormatCloudModelName(modelId);
            return StatusCode(502, failedResponse);
        }

        sw.Stop();

        if (cloudResponse.StatusCode != 200)
        {
            var errorEntry = await _history.AddAsync(
                modelId,
                resolved.Prompt,
                tokensPerSec: 0,
                latencyMs: sw.Elapsed.TotalMilliseconds,
                tokensGenerated: 0,
                status: "error",
                errorMessage: $"Upstream returned {cloudResponse.StatusCode}",
                ct,
                resolved.PromptId,
                resolved.PromptName,
                resolved.PromptVersion).ConfigureAwait(false);

            var errorResponse = BenchmarkResponse.FromEntry(errorEntry);
            errorResponse.ModelName = FormatCloudModelName(modelId);
            return StatusCode(cloudResponse.StatusCode, errorResponse);
        }

        // Read and parse the upstream response body.
        var (bodyJson, responseContent, responseReasoning, tokensGenerated) =
            await ParseCloudResponseBodyAsync(cloudResponse.Body, ct).ConfigureAwait(false);

        var elapsedMs = sw.Elapsed.TotalMilliseconds;
        var tokensPerSec = tokensGenerated > 0 && elapsedMs > 0
            ? tokensGenerated / (elapsedMs / 1000.0)
            : 0;

        var entry = await _history.AddAsync(
            modelId,
            resolved.Prompt,
            tokensPerSec,
            elapsedMs,
            tokensGenerated,
            status: "completed",
            errorMessage: null,
            ct,
            resolved.PromptId,
            resolved.PromptName,
            resolved.PromptVersion,
            responseContent,
            responseReasoning).ConfigureAwait(false);

        var responseItem = BenchmarkResponse.FromEntry(entry);
        responseItem.ModelName = FormatCloudModelName(modelId);
        return Ok(responseItem);
    }

    /// <summary>
    /// Resolve the prompt text and identity from the request body.
    /// Returns an <c>ErrorResult</c> when the referenced prompt is missing.
    /// </summary>
    private async Task<PromptResolution> ResolvePromptAsync(BenchmarkRunRequest? body, CancellationToken ct)
    {
        // Prompt resolution: PromptId → explicit Prompt text → store default → built-in const.
        // Identity is captured for benchmark-history attribution. Saved prompts carry
        // their own generation cap; ad-hoc text and the built-in const use the shared
        // BenchmarkDefaults.MaxTokens.
        string prompt;
        int maxTokens = MaxTokens;
        string? promptId = null;
        string? promptName = null;
        int? promptVersion = null;

        if (!string.IsNullOrWhiteSpace(body?.PromptId))
        {
            var promptEntry = await _prompts.GetAsync(body!.PromptId!.Trim(), ct);
            if (promptEntry is null)
                return new PromptResolution { ErrorResult = BadRequest(new { error = $"Prompt {body.PromptId} not found" }) };
            prompt = promptEntry.Text;
            maxTokens = promptEntry.MaxTokens;
            promptId = promptEntry.Id;
            promptName = promptEntry.Name;
            promptVersion = promptEntry.CurrentVersion;
        }
        else if (!string.IsNullOrWhiteSpace(body?.Prompt))
        {
            prompt = body!.Prompt!.Trim();
            // ad-hoc text prompt — no identity
        }
        else
        {
            var defaultEntry = await _prompts.GetDefaultAsync(ct);
            if (defaultEntry is not null && !string.IsNullOrWhiteSpace(defaultEntry.Text))
            {
                prompt = defaultEntry.Text;
                maxTokens = defaultEntry.MaxTokens;
                promptId = defaultEntry.Id;
                promptName = defaultEntry.Name;
                promptVersion = defaultEntry.CurrentVersion;
            }
            else
            {
                prompt = BenchmarkDefaults.DefaultBenchmarkPrompt;
                // built-in const — no identity
            }
        }

        return new PromptResolution
        {
            Prompt = prompt,
            MaxTokens = maxTokens,
            PromptId = promptId,
            PromptName = promptName,
            PromptVersion = promptVersion
        };
    }

    /// <summary>
    /// Parse a non-streaming cloud completion body, extracting content,
    /// reasoning, and usage tokens. Best-effort — never throws.
    /// </summary>
    private static async Task<(JsonNode? BodyJson, string? Content, string? Reasoning, int TokensGenerated)>
        ParseCloudResponseBodyAsync(Stream? body, CancellationToken ct)
    {
        if (body is null) return (null, null, null, 0);
        try
        {
            using var reader = new StreamReader(body);
            var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var bodyJson = JsonNode.Parse(raw);

            var (content, reasoning) = ExtractResponsePartsFromJson(bodyJson);
            var tokensGenerated = bodyJson?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;

            return (bodyJson, content, reasoning, tokensGenerated);
        }
        catch
        {
            return (null, null, null, 0);
        }
    }

    /// <summary>
    /// Extract content and reasoning_content from a parsed JSON response node.
    /// Mirrors <see cref="BenchmarkDefaults.ExtractResponsePartsAsync"/> but works
    /// with an already-parsed <see cref="JsonNode"/> instead of a stream.
    /// </summary>
    private static (string? Content, string? Reasoning) ExtractResponsePartsFromJson(JsonNode? json)
    {
        if (json is null) return (null, null);
        try
        {
            var message = json["choices"]?[0]?["message"];
            if (message is null) return (null, null);

            static string? ReadTruncated(JsonNode? node, string propertyName)
            {
                var text = node?[propertyName]?.GetValue<string>();
                if (string.IsNullOrEmpty(text)) return null;
                return text.Length <= MaxStoredResponseChars
                    ? text
                    : text[..MaxStoredResponseChars];
            }

            return (
                Content: ReadTruncated(message, "content"),
                Reasoning: ReadTruncated(message, "reasoning_content"));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Extract a human-readable model name from a cloud model ID.
    /// <c>cloud/openai/gpt-4o</c> → <c>openai/gpt-4o</c>.
    /// </summary>
    private static string FormatCloudModelName(string modelId)
    {
        var idx = modelId.IndexOf('/', StringComparison.Ordinal);
        return idx >= 0 ? modelId[(idx + 1)..] : modelId;
    }

    /// <summary>Internal record carrying the result of prompt resolution.</summary>
    private sealed record PromptResolution
    {
        public string Prompt { get; init; } = "";
        public int MaxTokens { get; init; } = BenchmarkDefaults.MaxTokens;
        public string? PromptId { get; init; }
        public string? PromptName { get; init; }
        public int? PromptVersion { get; init; }
        public IActionResult? ErrorResult { get; init; }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? modelId, CancellationToken ct)
    {
        var entries = await _history.ListAsync(50, modelId, ct).ConfigureAwait(false);
        var items = new List<BenchmarkResponse>(entries.Count);
        foreach (var entry in entries)
        {
            var item = BenchmarkResponse.FromEntry(entry);

            // Cloud models are not in the fleet registry — derive a display name
            // from the ID: "cloud/openai/gpt-4o" → "openai/gpt-4o".
            if (entry.ModelId.StartsWith("cloud/", StringComparison.Ordinal))
            {
                item.ModelName = FormatCloudModelName(entry.ModelId);
            }
            else
            {
                var model = await _registry.GetAsync(entry.ModelId, ct).ConfigureAwait(false);
                item.ModelName = model?.Name ?? entry.ModelId;
            }

            items.Add(item);
        }
        return Ok(items);
    }
}

public sealed class BenchmarkRunRequest
{
    public string? Prompt { get; set; }
    public string? PromptId { get; set; }
}
