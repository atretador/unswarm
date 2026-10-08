using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for the ModelsController registry surface: List, Get, Create,
/// Update (including the Conflict bookkeeping and the cloud-model branch) and
/// Delete. Action methods are called directly with hand-written fakes.
/// </summary>
public sealed class ModelsControllerTests
{
    private readonly FakeModelRegistry _registry = new();
    private readonly FakeBenchmarkHistory _benchmarks = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeCloudProviderStoreCoverage1 _cloudStore = new();
    private readonly FakeSchedulerQueue _scheduler = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLogStore _logs = new();
    private readonly FakeCloudForwardingService _cloudForwarding = new();
    private readonly FakeUsageRecorder _usage = new();

    private ModelsController CreateController() => new(
        _registry,
        _benchmarks,
        _containerRegistry,
        _cloudStore,
        _scheduler,
        _clock,
        _logs,
        _cloudForwarding,
        _usage);

    private async Task<ModelDefinition> SeedModelAsync(
        string id,
        string name,
        string? displayName = null,
        ModelStatus status = ModelStatus.Ready,
        string? sourceRuntimeId = null,
        string? inputModalitiesJson = null)
    {
        var model = new ModelDefinition
        {
            Id = id,
            Name = name,
            DisplayName = displayName,
            Status = status,
            SourceRuntimeId = sourceRuntimeId,
            InputModalitiesJson = inputModalitiesJson,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(model);
        return model;
    }

    // ── List ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsSwarmModelWithRuntimeInfoAndBenchmark()
    {
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "rt-1",
            DisplayName = "runtime-one",
            Image = "image:latest",
            Agent = "agent-x",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });
        await SeedModelAsync("m-1", "llama-3", sourceRuntimeId: "rt-1");
        await _benchmarks.AddAsync("m-1", "p", 12.5, 300, 25, "completed", null);

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var models = Assert.IsAssignableFrom<List<ModelResponse>>(ok.Value);
        var response = Assert.Single(models);
        Assert.Equal("m-1", response.Id);
        Assert.Equal("runtime-one", response.SourceRuntimeName);
        Assert.Equal("agent-x", response.SourceRuntimeAgent);
        Assert.NotNull(response.LastBenchmark);
        Assert.Equal(12.5, response.LastBenchmark!.TokensPerSec);
    }

    [Fact]
    public async Task List_AppendsCloudModelsFromProviders()
    {
        _cloudStore.Seed("prov-1", "openai", models:
        [
            new CloudProviderModelMeta
            {
                Id = "gpt-4o",
                ContextWindow = 128000,
                MaxOutputTokens = 16384,
                InputModalities = ["text", "image"]
            }
        ]);

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var models = Assert.IsAssignableFrom<List<ModelResponse>>(ok.Value);
        var cloud = Assert.Single(models);
        Assert.Equal("cloud/openai/gpt-4o", cloud.Id);
        Assert.Equal("cloud", cloud.Origin);
        Assert.Equal("openai", cloud.ProviderName);
        Assert.Equal(128000, cloud.ContextWindow);
        Assert.Contains("image", cloud.InputModalities);
    }

    [Fact]
    public async Task List_ReturnsEmptyWhenNoModels()
    {
        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsAssignableFrom<List<ModelResponse>>(ok.Value));
    }

    // ── Get ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Existing_ReturnsModel()
    {
        await SeedModelAsync("m-1", "llama-3");

        var result = await CreateController().Get("m-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ModelResponse>(ok.Value);
        Assert.Equal("m-1", response.Id);
    }

    [Fact]
    public async Task Get_Missing_ReturnsNotFound()
    {
        var result = await CreateController().Get("nope", CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_LeadingSlashFallback_ResolvesPrefixedId()
    {
        await SeedModelAsync("/models/llama-3", "llama-3");

        var result = await CreateController().Get("models/llama-3", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ModelResponse>(ok.Value);
        Assert.Equal("/models/llama-3", response.Id);
    }

    // ── Create ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Valid_Returns201AndPersists()
    {
        var request = new ModelCreateRequest
        {
            Name = "llama-3",
            Family = "llama",
            ContextWindow = 8192,
            InputModalities = ["text", "image"]
        };

        var result = await CreateController().Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var response = Assert.IsType<ModelResponse>(created.Value);
        Assert.Equal("llama-3", response.Name);
        Assert.Equal(8192, response.ContextWindow);
        Assert.Contains("image", response.InputModalities);
        Assert.Single(_registry.CreatedModels);
        Assert.NotNull(_registry.CreatedModels[0].InputModalitiesJson);
    }

    [Fact]
    public async Task Create_NullModalities_LeavesJsonNull()
    {
        var request = new ModelCreateRequest { Name = "m", ContextWindow = 4096 };

        var result = await CreateController().Create(request, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Null(_registry.CreatedModels[0].InputModalitiesJson);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_000_001)]
    public async Task Create_ContextWindowOutOfRange_Returns400(int contextWindow)
    {
        var request = new ModelCreateRequest { Name = "m", ContextWindow = contextWindow };

        var result = await CreateController().Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_registry.CreatedModels);
    }

    [Fact]
    public async Task Create_UnknownModality_Returns400()
    {
        var request = new ModelCreateRequest
        {
            Name = "m",
            ContextWindow = 4096,
            InputModalities = ["text", "bogus"]
        };

        var result = await CreateController().Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Update ───────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Valid_ReturnsUpdatedModel()
    {
        await SeedModelAsync("m-1", "llama-3");

        var result = await CreateController().Update("m-1",
            new ModelUpdateRequest { Name = "llama-3.1", ContextWindow = 16384 },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ModelResponse>(ok.Value);
        Assert.Equal("llama-3.1", response.Name);
        Assert.Equal(16384, response.ContextWindow);
    }

    [Fact]
    public async Task Update_Missing_ReturnsNotFound()
    {
        var result = await CreateController().Update("nope",
            new ModelUpdateRequest { Name = "x" }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20_000_000)]
    public async Task Update_InvalidContextWindow_Returns400(int contextWindow)
    {
        await SeedModelAsync("m-1", "llama-3");

        var result = await CreateController().Update("m-1",
            new ModelUpdateRequest { ContextWindow = contextWindow }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_UnknownModality_Returns400()
    {
        await SeedModelAsync("m-1", "llama-3");

        var result = await CreateController().Update("m-1",
            new ModelUpdateRequest { InputModalities = ["nope"] }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_DuplicateDisplayName_FlagsAllAsConflict()
    {
        await SeedModelAsync("m-1", "name-a", displayName: "shared");
        await SeedModelAsync("m-2", "name-b", displayName: "shared");

        var result = await CreateController().Update("m-1",
            new ModelUpdateRequest { Name = "name-a" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ModelResponse>(ok.Value);
        Assert.Equal(ModelStatus.Conflict, response.Status);
        Assert.Equal(ModelStatus.Conflict, (await _registry.GetAsync("m-2"))!.Status);
    }

    [Fact]
    public async Task Update_SingleModelInConflict_ResolvesToReady()
    {
        await SeedModelAsync("m-1", "name-a", displayName: "unique", status: ModelStatus.Conflict);

        var result = await CreateController().Update("m-1",
            new ModelUpdateRequest { Name = "name-a" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ModelResponse>(ok.Value);
        Assert.Equal(ModelStatus.Ready, response.Status);
    }

    // ── Update: cloud branch (UpdateCloudModelAsync) ─────────────────

    [Fact]
    public async Task Update_CloudModel_UpdatesMetadata()
    {
        _cloudStore.Seed("prov-1", "openai", models:
        [
            new CloudProviderModelMeta { Id = "gpt-4o", ContextWindow = 128000, MaxOutputTokens = 4096 }
        ]);

        var result = await CreateController().Update("cloud/openai/gpt-4o",
            new ModelUpdateRequest { ContextWindow = 256000, MaxOutputTokens = 8192, InputModalities = ["text", "image"] },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ModelResponse>(ok.Value);
        Assert.Equal("cloud/openai/gpt-4o", response.Id);
        Assert.Equal(256000, response.ContextWindow);
        Assert.Equal(8192, response.MaxOutputTokens);
        Assert.Contains("image", response.InputModalities);
        Assert.Single(_cloudStore.SavedModels);
    }

    [Fact]
    public async Task Update_CloudProviderMissing_ReturnsNotFound()
    {
        var result = await CreateController().Update("cloud/nope/gpt-4o",
            new ModelUpdateRequest { ContextWindow = 4096 }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_CloudModelMissing_ReturnsNotFound()
    {
        _cloudStore.Seed("prov-1", "openai");

        var result = await CreateController().Update("cloud/openai/nope",
            new ModelUpdateRequest { ContextWindow = 4096 }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_CloudSaveRejects_Returns400()
    {
        _cloudStore.Seed("prov-1", "openai", models: [new CloudProviderModelMeta { Id = "gpt-4o" }]);
        _cloudStore.SaveModelsException = new ArgumentException("model list invalid");

        var result = await CreateController().Update("cloud/openai/gpt-4o",
            new ModelUpdateRequest { ContextWindow = 4096 }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Existing_ReturnsNoContent()
    {
        await SeedModelAsync("m-1", "llama-3");

        var result = await CreateController().Delete("m-1", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("m-1", _registry.DeletedModelIds);
        Assert.Null(await _registry.GetAsync("m-1"));
    }

    [Fact]
    public async Task Delete_Missing_ReturnsNotFound()
    {
        var result = await CreateController().Delete("nope", CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_LeadingSlashFallback_DeletesPrefixedId()
    {
        await SeedModelAsync("/models/llama-3", "llama-3");

        var result = await CreateController().Delete("models/llama-3", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("/models/llama-3", _registry.DeletedModelIds);
    }
}
