using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class BenchmarksControllerTests
{
    private readonly FakeModelRegistry _modelRegistry = new();
    private readonly FakeSchedulerQueue _scheduler = new();
    private readonly FakeBenchmarkHistory _history = new();
    private readonly FakeClock _clock = new();
    private readonly FakePromptStore _prompts = new();

    private async Task<ModelDefinition> SeedModel(string id = "model-1", string name = "llama-3")
    {
        var model = new ModelDefinition
        {
            Id = id,
            Name = name,
            Status = ModelStatus.Ready,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _modelRegistry.CreateAsync(model);
        return model;
    }

    private BenchmarksController CreateController() =>
        new(_modelRegistry, _scheduler, _clock, _history, _prompts, new FakeCloudForwardingService());

    private BenchmarksController CreateController(ICloudForwardingService cloudForwarding) =>
        new(_modelRegistry, _scheduler, _clock, _history, _prompts, cloudForwarding);

    [Fact]
    public async Task Run_PersistsAndReturnsFullCompletedItem()
    {
        var model = await SeedModel();
        _scheduler.DefaultResponse = new InferenceResponse
        {
            StatusCode = 200,
            ContentType = "application/json",
            TokensGenerated = 64
        };

        var controller = CreateController();
        var result = await controller.Run(model.Id, new BenchmarkRunRequest { Prompt = "Tell me a story" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.IsType<BenchmarkResponse>(ok.Value);

        Assert.Equal("completed", item.Status);
        Assert.Equal(model.Id, item.ModelId);
        Assert.Equal("Tell me a story", item.Prompt);
        Assert.Equal(64, item.TokensGenerated);
        Assert.True(item.TokensPerSec > 0);
        Assert.NotNull(item.Id);

        // The request went through the scheduler with the given prompt
        var request = Assert.Single(_scheduler.EnqueuedRequests);
        Assert.False(request.IsStreaming);
        Assert.Contains("Tell me a story", request.OriginalJson);
        Assert.Contains("\"max_tokens\":256", request.OriginalJson);

        // Persisted
        var persisted = Assert.Single(_history.Entries);
        Assert.Equal("completed", persisted.Status);
        Assert.Equal(64, persisted.TokensGenerated);
    }

    [Fact]
    public async Task Run_DefaultPrompt_WhenBodyMissing()
    {
        var model = await SeedModel();

        var controller = CreateController();
        var result = await controller.Run(model.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.IsType<BenchmarkResponse>(ok.Value);
        Assert.Equal("completed", item.Status);
        Assert.NotNull(item.Prompt);
        Assert.Contains("Write a detailed summary", item.Prompt);
    }

    [Fact]
    public async Task Run_ModelNotFound_ReturnsNotFound()
    {
        var controller = CreateController();
        var result = await controller.Run("nope", null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task Run_SchedulerThrows_PersistsErrorEntry_Returns502WithItem()
    {
        var model = await SeedModel();
        _scheduler.EnqueueFunc = (req, ct) => throw new InvalidOperationException("container not available");

        var controller = CreateController();
        var result = await controller.Run(model.Id, null, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);

        var item = Assert.IsType<BenchmarkResponse>(status.Value);
        Assert.Equal("error", item.Status);
        Assert.NotNull(item.ErrorMessage);
        Assert.Contains("container not available", item.ErrorMessage);

        // Failure is persisted too
        var persisted = Assert.Single(_history.Entries);
        Assert.Equal("error", persisted.Status);
    }

    [Fact]
    public async Task List_ReturnsHistory_NewestFirst()
    {
        await _history.AddAsync("model-1", "p1", 1, 1, 1, "completed", null);
        await _history.AddAsync("model-1", "p2", 2, 2, 2, "completed", null);

        var controller = CreateController();
        var result = await controller.List(null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<BenchmarkResponse>>(ok.Value);

        Assert.Equal(2, items.Count);
        Assert.Equal("p2", items[0].Prompt); // newest first
        Assert.Equal("p1", items[1].Prompt);
    }

    // ── Prompt resolution ─────────────────────────────────────────────

    [Fact]
    public async Task Run_WithPromptId_UsesSavedPromptTextAndCap()
    {
        var model = await SeedModel();
        var prompt = await _prompts.CreateAsync("Saved", "Do the thing", maxTokens: 128);

        var controller = CreateController();
        var result = await controller.Run(model.Id, new BenchmarkRunRequest { PromptId = prompt.Id }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.IsType<BenchmarkResponse>(ok.Value);
        Assert.Equal("completed", item.Status);

        var request = Assert.Single(_scheduler.EnqueuedRequests);
        Assert.Contains("Do the thing", request.OriginalJson);
        Assert.Contains("\"max_tokens\":128", request.OriginalJson);
    }

    [Fact]
    public async Task Run_UnknownPromptId_ReturnsBadRequest()
    {
        var model = await SeedModel();

        var controller = CreateController();
        var result = await controller.Run(model.Id, new BenchmarkRunRequest { PromptId = "missing" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task Run_SwarmCancelled_Returns499()
    {
        var model = await SeedModel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _scheduler.EnqueueFunc = (_, ct) => throw new OperationCanceledException();

        var controller = CreateController();
        var result = await controller.Run(model.Id, null, cts.Token);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(499, status.StatusCode);
    }

    // ── Cloud benchmark path ──────────────────────────────────────────

    [Fact]
    public async Task Run_CloudModel_ParsesUpstreamResponseAndPersistsContent()
    {
        var cloud = new FakeCloudForwardingCoverage2
        {
            BodyBytes = System.Text.Encoding.UTF8.GetBytes(
                """{"choices":[{"message":{"content":"the answer","reasoning_content":"the reasoning"}}],"usage":{"completion_tokens":7}}""")
        };

        var controller = CreateController(cloud);
        var result = await controller.Run("cloud/openai/gpt-4o", new BenchmarkRunRequest { Prompt = "Hi" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.IsType<BenchmarkResponse>(ok.Value);
        Assert.Equal("completed", item.Status);
        Assert.Equal("openai/gpt-4o", item.ModelName);

        var entry = Assert.Single(_history.Entries);
        Assert.Equal("the answer", entry.Response);
        Assert.Equal("the reasoning", entry.Reasoning);
        Assert.Equal(7, entry.TokensGenerated);

        var forwarded = Assert.Single(cloud.Forwarded);
        Assert.Equal("cloud/openai/gpt-4o", forwarded.ModelId);
        Assert.Equal("/v1/chat/completions", forwarded.RequestPath);
        Assert.False(forwarded.IsStreaming);
    }

    [Fact]
    public async Task Run_CloudModel_UpstreamError_ReturnsUpstreamStatusAndErrorEntry()
    {
        var cloud = new FakeCloudForwardingCoverage2 { StatusCode = 503 };

        var controller = CreateController(cloud);
        var result = await controller.Run("cloud/openai/gpt-4o", null, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
        var item = Assert.IsType<BenchmarkResponse>(status.Value);
        Assert.Equal("error", item.Status);
        Assert.Equal("openai/gpt-4o", item.ModelName);
    }

    [Fact]
    public async Task Run_CloudModel_TransportFailure_Returns502()
    {
        var cloud = new FakeCloudForwardingCoverage2 { Exception = new HttpRequestException("boom") };

        var controller = CreateController(cloud);
        var result = await controller.Run("cloud/openai/gpt-4o", null, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        var item = Assert.IsType<BenchmarkResponse>(status.Value);
        Assert.Equal("error", item.Status);
        Assert.Single(_history.Entries);
    }

    [Fact]
    public async Task Run_CloudModel_Cancelled_Returns499()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cloud = new FakeCloudForwardingCoverage2 { Exception = new OperationCanceledException() };

        var controller = CreateController(cloud);
        var result = await controller.Run("cloud/openai/gpt-4o", null, cts.Token);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(499, status.StatusCode);
    }

    [Fact]
    public async Task List_CloudModel_UsesDerivedDisplayName()
    {
        await _history.AddAsync("cloud/openai/gpt-4o", "p", 1, 1, 1, "completed", null);

        var controller = CreateController();
        var result = await controller.List(null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<BenchmarkResponse>>(ok.Value);
        var item = Assert.Single(items);
        Assert.Equal("openai/gpt-4o", item.ModelName);
        Assert.Equal("openai/gpt-4o", item.ModelDisplayName);
    }
}
