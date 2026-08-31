using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for POST /api/models/test-chat — the admin test-chat endpoint that
/// routes interactive chat turns through the same pipeline as /v1 traffic.
/// </summary>
public sealed class ModelsControllerTestChatTests
{
    private readonly FakeModelRegistry _modelRegistry = new();
    private readonly FakeSchedulerQueue _scheduler = new();
    private readonly FakeCloudForwardingService _cloud = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLogStore _logs = new();
    private readonly FakeUsageRecorder _usage = new();

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

    private ModelsController CreateController()
    {
        var controller = new ModelsController(
            _modelRegistry,
            new FakeBenchmarkHistory(),
            new FakeContainerRegistry(),
            new StubCloudProviderStore(),
            _scheduler,
            _clock,
            _logs,
            _cloud,
            _usage);
        // The endpoint writes to the response body (streaming passthrough).
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    /// <summary>Minimal ICloudProviderStore for controller tests (no providers).</summary>
    private sealed class StubCloudProviderStore : Unswarm.Core.Contracts.ICloudProviderStore
    {
        public Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudProviderListItem>>([]);
        public Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<CloudProviderReadItem?>(null);
        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<CloudProviderReadItem?>(null);
        public Task<bool> NameExistsAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static TestChatRequest Turn(string model = "model-1", bool stream = false, string content = "hi") => new()
    {
        Model = model,
        Messages = [new TestChatMessage { Role = "user", Content = content }],
        Stream = stream
    };

    // ── Validation ───────────────────────────────────────────────────

    [Fact]
    public async Task TestChat_MissingBody_Returns400()
    {
        var result = await CreateController().TestChat(null, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("model", bad.Value!.ToString());
    }

    [Fact]
    public async Task TestChat_BlankModel_Returns400()
    {
        var result = await CreateController().TestChat(Turn(model: "  "), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestChat_EmptyMessages_Returns400()
    {
        var request = new TestChatRequest { Model = "model-1", Messages = [] };
        var result = await CreateController().TestChat(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestChat_WhitespaceOnlyMessages_Returns400()
    {
        var request = new TestChatRequest
        {
            Model = "model-1",
            Messages = [new TestChatMessage { Role = "user", Content = "   " }]
        };
        var result = await CreateController().TestChat(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Swarm path ───────────────────────────────────────────────────

    [Fact]
    public async Task TestChat_UnknownSwarmModel_Returns404()
    {
        var result = await CreateController().TestChat(Turn(), CancellationToken.None);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("model-1", notFound.Value!.ToString());
    }

    [Fact]
    public async Task TestChat_SwarmModel_RoutesThroughSchedulerWithRegistryName()
    {
        var model = await SeedModel(id: "model-1", name: "llama-3");

        var result = await CreateController().TestChat(Turn(stream: false), CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var request = Assert.Single(_scheduler.EnqueuedRequests);
        Assert.Equal("llama-3", request.ModelName);
        Assert.False(request.IsStreaming);

        // The engine-visible payload mirrors /v1: registry name + OpenAI fields.
        var json = JsonDocument.Parse(request.OriginalJson).RootElement;
        Assert.Equal("llama-3", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("stream").GetBoolean());
        Assert.Equal("hi", json.GetProperty("messages")[0].GetProperty("content").GetString());

        // Usage attributed to the local provider kind, no API key.
        var record = Assert.Single(_usage.Records);
        Assert.Equal("llama-3", record.Model);
        Assert.Equal("local", record.ProviderKind);
        Assert.Null(record.ApiKeyId);

        // The turn is visible in the log store under the test-chat source.
        Assert.Contains(_logs.Entries, e => e.Source == "test-chat");
    }

    [Fact]
    public async Task TestChat_SystemPrompt_IsPrependedAsSystemMessage()
    {
        await SeedModel();
        var request = Turn(stream: false);
        request.System = "You are terse.";

        await CreateController().TestChat(request, CancellationToken.None);

        var request0 = Assert.Single(_scheduler.EnqueuedRequests);
        var messages = JsonDocument.Parse(request0.OriginalJson).RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are terse.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task TestChat_MaxTokensAndTemperature_AreClampedIntoPayload()
    {
        await SeedModel();
        var request = Turn(stream: false);
        request.MaxTokens = 999_999;
        request.Temperature = 5;

        await CreateController().TestChat(request, CancellationToken.None);

        var enqueued = Assert.Single(_scheduler.EnqueuedRequests);
        var json = JsonDocument.Parse(enqueued.OriginalJson).RootElement;
        Assert.Equal(32768, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal(2.0, json.GetProperty("temperature").GetDouble());
    }

    // ── Cloud path ───────────────────────────────────────────────────

    [Fact]
    public async Task TestChat_CloudModel_ForwardsToProviderWithoutScheduler()
    {
        var result = await CreateController()
            .TestChat(Turn(model: "cloud/openai/gpt-4o", stream: true), CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Empty(_scheduler.EnqueuedRequests);

        var forward = Assert.Single(_cloud.Forwarded);
        Assert.Equal("cloud/openai/gpt-4o", forward.ModelId);
        Assert.Equal("/v1/chat/completions", forward.RequestPath);
        Assert.True(forward.IsStreaming);
        Assert.True(JsonDocument.Parse(forward.RequestBody).RootElement.GetProperty("stream").GetBoolean());

        var record = Assert.Single(_usage.Records);
        Assert.Equal("openai", record.Provider); // extracted from the cloud/ id
        Assert.Equal("cloud", record.ProviderKind);
    }
}
