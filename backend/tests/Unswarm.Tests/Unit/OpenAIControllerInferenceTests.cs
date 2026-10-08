using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Api.Services;
using Unswarm.Core.Models;
using Unswarm.Core.Contracts;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for OpenAIController's /v1 surface: ListModels, completions, and
/// the shared inference pipeline (swarm scheduler, cloud API-key, cloud
/// subscription, router profile, validation and permission branches).
/// Action methods are called directly; the HTTP context is a DefaultHttpContext.
/// </summary>
public sealed class OpenAIControllerInferenceTests
{
    private readonly FakeModelRegistry _registry = new();
    private readonly FakeSchedulerQueue _scheduler = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLogStore _logs = new();
    private readonly FakeCloudForwardingService _cloudForwarding = new();
    private readonly FakeCloudProviderStoreCoverage1 _cloudStore = new();
    private readonly FakeChatGptSubscriptionCoverage1 _chatGpt = new();
    private readonly FakeUsageRecorder _usage = new();
    private readonly FakeApiKeyAccessCoverage1 _apiKeyAccess = new();
    private readonly FakeRouterProfileServiceCoverage1 _routerProfile = new();

    private MemoryStream _responseBody = new();
    private DefaultHttpContext _context = new();

    private OpenAIController CreateController(
        string? body = null,
        string? keyId = null,
        ICloudForwardingService? cloudForwarding = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        if (keyId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("unswarm:key-id", keyId), new Claim(ClaimTypes.Name, "my-key")], "test"));
        }

        if (body is not null)
        {
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentType = "application/json";
        }

        _responseBody = new MemoryStream();
        context.Response.Body = _responseBody;
        _context = context;

        var forwarding = cloudForwarding ?? _cloudForwarding;
        var handler = new RouterProfileHandler(
            _routerProfile,
            forwarding,
            _scheduler,
            _logs,
            _clock,
            new FakeSettingsStore(),
            new RouterProfileActivityTracker());

        var controller = new OpenAIController(
            _registry,
            _scheduler,
            _clock,
            _logs,
            forwarding,
            _cloudStore,
            _chatGpt,
            _usage,
            _apiKeyAccess,
            _routerProfile,
            handler)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        return controller;
    }

    private static string ReadBody(MemoryStream body)
    {
        body.Position = 0;
        return new StreamReader(body).ReadToEnd();
    }

    private async Task<ModelDefinition> SeedModelAsync(
        string id,
        string name,
        ModelStatus status = ModelStatus.Ready,
        string? displayName = null)
    {
        var model = new ModelDefinition
        {
            Id = id,
            Name = name,
            DisplayName = displayName,
            Status = status,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(model);
        return model;
    }

    // ── ListModels ────────────────────────────────────────────────────

    [Fact]
    public async Task ListModels_IncludesSwarmCloudAndRouterModels()
    {
        await SeedModelAsync("m-1", "llama-3");
        _cloudStore.Seed("prov-1", "openai", models:
        [
            new CloudProviderModelMeta { Id = "gpt-4o", ContextWindow = 128000 }
        ]);
        _routerProfile.Profiles.Add(new RouterProfile
        {
            Id = "p-1",
            Name = "Fast",
            Mode = RouterProfileMode.Auto,
            Entries = [new RouterProfileEntry { ModelId = "llama-3", Priority = 0 }],
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });

        var result = await CreateController().ListModels(CancellationToken.None);

        var response = Assert.IsType<OpenAiModelListResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        var ids = response.Data.Select(d => d.Id).ToList();
        Assert.Contains("llama-3", ids);
        Assert.Contains("cloud/openai/gpt-4o", ids);
        Assert.Contains("router/Fast", ids);
    }

    [Fact]
    public async Task ListModels_ExcludesConflictModels()
    {
        await SeedModelAsync("m-1", "llama-3");
        await SeedModelAsync("m-2", "broken", status: ModelStatus.Conflict);

        var result = await CreateController().ListModels(CancellationToken.None);

        var response = Assert.IsType<OpenAiModelListResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        var ids = response.Data.Select(d => d.Id).ToList();
        Assert.Contains("llama-3", ids);
        Assert.DoesNotContain("broken", ids);
    }

    [Fact]
    public async Task ListModels_WithApiKeyRestriction_FiltersModelIds()
    {
        await SeedModelAsync("m-1", "llama-3");
        await SeedModelAsync("m-2", "qwen");
        _apiKeyAccess.FilterFunc = (_, _) => ["llama-3"];

        var result = await CreateController(keyId: "k1").ListModels(CancellationToken.None);

        var response = Assert.IsType<OpenAiModelListResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(["llama-3"], response.Data.Select(d => d.Id).ToList());
    }

    // ── Validation / permission ───────────────────────────────────────

    [Fact]
    public async Task ChatCompletions_InvalidJson_Returns400()
    {
        var result = await CreateController("not-json").ChatCompletions(CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ChatCompletions_ApiKeyNotAllowed_Returns403()
    {
        _apiKeyAccess.Allowed = false;

        var result = await CreateController("{\"model\":\"llama-3\"}", keyId: "k1")
            .ChatCompletions(CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
        Assert.Empty(_scheduler.EnqueuedRequests);
    }

    // ── Swarm scheduler path ──────────────────────────────────────────

    [Fact]
    public async Task Completions_SwarmModel_RoutesThroughScheduler()
    {
        await SeedModelAsync("m-1", "llama-3");

        var result = await CreateController("{\"model\":\"llama-3\"}").Completions(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var request = Assert.Single(_scheduler.EnqueuedRequests);
        Assert.Equal("llama-3", request.ModelName);
        var record = Assert.Single(_usage.Records);
        Assert.Equal("llama-3", record.Model);
        Assert.Equal("local", record.ProviderKind);
    }

    [Fact]
    public async Task ChatCompletions_SchedulerThrows_Returns502()
    {
        _scheduler.EnqueueFunc = (_, _) => throw new InvalidOperationException("queue down");

        var result = await CreateController("{\"model\":\"llama-3\"}").ChatCompletions(CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_SchedulerCancelled_Returns499()
    {
        _scheduler.EnqueueFunc = (_, _) => throw new OperationCanceledException();

        var result = await CreateController("{\"model\":\"llama-3\"}").ChatCompletions(CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(499, status.StatusCode);
    }

    // ── Cloud API-key path ────────────────────────────────────────────

    [Fact]
    public async Task ChatCompletions_CloudApiKeyProvider_ForwardsToProvider()
    {
        _cloudStore.Seed("prov-1", "openai", authType: 0);

        var result = await CreateController("{\"model\":\"cloud/openai/gpt-4o\",\"stream\":false}")
            .ChatCompletions(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var forward = Assert.Single(_cloudForwarding.Forwarded);
        Assert.Equal("cloud/openai/gpt-4o", forward.ModelId);
        Assert.Equal("/v1/chat/completions", forward.RequestPath);
        Assert.Equal(200, _context.Response.StatusCode);
        var record = Assert.Single(_usage.Records);
        Assert.Equal("openai", record.Provider);
        Assert.Equal("cloud", record.ProviderKind);
    }

    [Fact]
    public async Task ChatCompletions_CloudForwardingThrows_Returns502()
    {
        _cloudStore.Seed("prov-1", "openai", authType: 0);
        var throwing = new FakeThrowingCloudForwardingCoverage1
        {
            Exception = new InvalidOperationException("upstream down")
        };

        var result = await CreateController("{\"model\":\"cloud/openai/gpt-4o\"}", cloudForwarding: throwing)
            .ChatCompletions(CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_CloudForwardingCancelled_Returns499()
    {
        _cloudStore.Seed("prov-1", "openai", authType: 0);
        var throwing = new FakeThrowingCloudForwardingCoverage1
        {
            Exception = new OperationCanceledException()
        };

        var result = await CreateController("{\"model\":\"cloud/openai/gpt-4o\"}", cloudForwarding: throwing)
            .ChatCompletions(CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(499, status.StatusCode);
    }

    // ── Cloud subscription path ───────────────────────────────────────

    [Fact]
    public async Task ChatCompletions_ChatGptSubscriptionProvider_PipesStream()
    {
        _cloudStore.Seed("prov-1", "openai", authType: 1);
        _chatGpt.Body = "data: hello\n\n"u8.ToArray();

        var result = await CreateController("{\"model\":\"cloud/openai/gpt-5\",\"stream\":true}")
            .ChatCompletions(CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var forward = Assert.Single(_chatGpt.Forwarded);
        Assert.Equal("cloud/openai/gpt-5", forward.ModelId);
        Assert.Equal("text/event-stream", _context.Response.ContentType);
        Assert.Contains("data: hello", ReadBody(_responseBody));
        var record = Assert.Single(_usage.Records);
        Assert.Equal("openai", record.Provider);
        Assert.Equal("cloud", record.ProviderKind);
    }

    // ── Router profile path ───────────────────────────────────────────

    [Fact]
    public async Task ChatCompletions_RouterProfileNotFound_Returns404()
    {
        // ResolveFunc left null → handler resolves no profile → 404 + no body.
        var result = await CreateController("{\"model\":\"router/Missing\"}")
            .ChatCompletions(CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_RouterHandlerThrows_Returns502()
    {
        _routerProfile.ResolveFunc = _ => throw new InvalidOperationException("router exploded");

        var result = await CreateController("{\"model\":\"router/Fast\"}")
            .ChatCompletions(CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
    }
}
