using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class BenchmarksController : ControllerBase
{
    private readonly IModelRegistry _registry;
    private readonly ISchedulerQueue _scheduler;
    private readonly IClock _clock;

    private const string SmokePayload = """{"model":"benchmark","messages":[{"role":"user","content":"Say hello"}],"max_tokens":10}""";

    public BenchmarksController(IModelRegistry registry, ISchedulerQueue scheduler, IClock clock)
    {
        _registry = registry;
        _scheduler = scheduler;
        _clock = clock;
    }

    [HttpPost("{modelId}")]
    public async Task<IActionResult> Run(string modelId, CancellationToken ct)
    {
        var model = await _registry.GetAsync(modelId, ct);
        if (model is null) return NotFound(new { error = $"Model {modelId} not found" });

        var request = new InferenceRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = model.Name,
            OriginalJson = SmokePayload,
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
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Benchmark failed: {ex.Message}" });
        }

        sw.Stop();
        var elapsedMs = sw.Elapsed.TotalMilliseconds;

        var result = new BenchmarkResult
        {
            TokensPerSec = response.TokensGenerated > 0
                ? response.TokensGenerated / (elapsedMs / 1000.0)
                : 0,
            LatencyMs = elapsedMs,
            Timestamp = _clock.UtcNow
        };

        return Ok(BenchmarkResponse.FromResult(result));
    }
}
