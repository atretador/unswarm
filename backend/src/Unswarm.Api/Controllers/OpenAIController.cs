using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.Controllers;

[ApiController]
// Inference surface: an OpenAI-compatible proxy. A managed inference API key
// authenticates here; the admin cookie is also accepted for local testing.
[Authorize(Policy = "InferenceKey")]
// [Authorize(Policy = "Cookie")]
[Route("v1")]
public sealed class OpenAIController : ControllerBase
{
    private readonly IModelRegistry _registry;
    private readonly ISchedulerQueue _scheduler;
    private readonly IClock _clock;
    private readonly ILogStore _logStore;

    public OpenAIController(IModelRegistry registry, ISchedulerQueue scheduler, IClock clock, ILogStore logStore)
    {
        _registry = registry;
        _scheduler = scheduler;
        _clock = clock;
        _logStore = logStore;
    }

    [HttpGet("models")]
    public async Task<IActionResult> ListModels(CancellationToken ct)
    {
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
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        string modelName;
        bool isStream;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            modelName = root.GetProperty("model").GetString() ?? "";
            isStream = root.TryGetProperty("stream", out var streamProp) && streamProp.GetBoolean();
        }
        catch
        {
            return BadRequest(new { error = "Invalid JSON: 'model' field required" });
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
            CancellationToken = ct
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

        return new EmptyResult();
    }
}
