using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

[ApiController]
[Route("v1")]
public sealed class OpenAIController : ControllerBase
{
    private readonly IModelRegistry _registry;
    private readonly ISchedulerQueue _scheduler;
    private readonly IClock _clock;

    public OpenAIController(IModelRegistry registry, ISchedulerQueue scheduler, IClock clock)
    {
        _registry = registry;
        _scheduler = scheduler;
        _clock = clock;
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

        InferenceResponse inferenceResponse;
        try
        {
            inferenceResponse = await _scheduler.EnqueueAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }

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
            await inferenceResponse.Body.CopyToAsync(Response.Body, ct);
            await Response.Body.FlushAsync(ct);
        }

        return new EmptyResult();
    }
}
