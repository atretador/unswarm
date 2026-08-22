using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Benchmarks;

namespace Unswarm.Api.Controllers;

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

    // Shared built-in prompt + max-token cap live in BenchmarkDefaults (Core) so the
    // automatic post-registration benchmark uses the exact same values.
    private const int MaxTokens = BenchmarkDefaults.MaxTokens;

    public BenchmarksController(
        IModelRegistry registry,
        ISchedulerQueue scheduler,
        IClock clock,
        IBenchmarkHistory history,
        IPromptStore prompts)
    {
        _registry = registry;
        _scheduler = scheduler;
        _clock = clock;
        _history = history;
        _prompts = prompts;
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Run([FromQuery] string modelId, [FromBody] BenchmarkRunRequest? body, CancellationToken ct)
    {
        var model = await _registry.GetAsync(modelId, ct);
        if (model is null) return NotFound(new { error = $"Model {modelId} not found" });

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
                return BadRequest(new { error = $"Prompt {body.PromptId} not found" });
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

        var requestJson = BenchmarkDefaults.BuildChatPayload(model.Id, prompt, maxTokens);

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
                prompt,
                tokensPerSec: 0,
                latencyMs: sw.Elapsed.TotalMilliseconds,
                tokensGenerated: 0,
                status: "error",
                errorMessage: ex.Message,
                ct,
                promptId,
                promptName,
                promptVersion).ConfigureAwait(false);

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

        // Capture the model's answer text for history. Best-effort: null on any
        // read/parse failure, never throws.
        var responseText = await BenchmarkDefaults.ExtractResponseContentAsync(response.Body, ct).ConfigureAwait(false);

        var entry = await _history.AddAsync(
            model.Id,
            prompt,
            tokensPerSec,
            elapsedMs,
            tokensGenerated,
            status: "completed",
            errorMessage: null,
            ct,
            promptId,
            promptName,
            promptVersion,
            responseText).ConfigureAwait(false);

        var responseItem = BenchmarkResponse.FromEntry(entry);
        responseItem.ModelName = model.Name;
        return Ok(responseItem);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? modelId, CancellationToken ct)
    {
        var entries = await _history.ListAsync(50, modelId, ct).ConfigureAwait(false);
        var items = new List<BenchmarkResponse>(entries.Count);
        foreach (var entry in entries)
        {
            var item = BenchmarkResponse.FromEntry(entry);
            var model = await _registry.GetAsync(entry.ModelId, ct).ConfigureAwait(false);
            item.ModelName = model?.Name ?? entry.ModelId;
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
