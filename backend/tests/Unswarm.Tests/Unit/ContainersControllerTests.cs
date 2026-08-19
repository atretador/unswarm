using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ContainersControllerTests
{
    private readonly FakeDockerController _docker = new();
    private readonly FakeModelRegistry _modelRegistry = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistrationService _registrationService = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeBenchmarkHistory _benchmarks = new();

    private ContainersController CreateController() => new(
        _docker,
        _modelRegistry,
        _clock,
        _registrationService,
        _containerRegistry,
        _benchmarks);

    private static RegisteredContainer MakeContainer(string id, string image = "test:latest") => new()
    {
        Id = id,
        DisplayName = image,
        Image = image,
        ContainerPort = 8080,
        Agent = "host",
        Status = ContainerRegistrationStatus.Ready,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private async Task<ModelDefinition> SeedModelAsync(string id, string name)
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

    private async Task<(RegisteredContainer Container, ModelDefinition Model)> SeedRegisteredWithModelAsync(
        string containerId,
        string modelId,
        string modelName)
    {
        var container = MakeContainer(containerId);
        await _containerRegistry.CreateAsync(container);
        var model = await SeedModelAsync(modelId, modelName);
        await _containerRegistry.AddModelMappingAsync(container.Id, model.Id);
        return (container, model);
    }

    [Fact]
    public async Task ListRegistered_PopulatesLastBenchmark_ForModelWithHistory()
    {
        var (_, model) = await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");
        await _benchmarks.AddAsync("model-1", "p1", 12.5, 300, 25, "completed", null);

        var result = await CreateController().ListRegistered(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var containers = Assert.IsAssignableFrom<List<RegisteredContainerResponse>>(ok.Value);

        var response = Assert.Single(containers);
        var discovered = Assert.Single(response.DiscoveredModels);
        Assert.Equal("model-1", discovered.Id);
        Assert.NotNull(discovered.LastBenchmark);
        Assert.Equal(12.5, discovered.LastBenchmark!.TokensPerSec);
        Assert.Equal(300, discovered.LastBenchmark.LatencyMs);
    }

    [Fact]
    public async Task ListRegistered_LeavesLastBenchmarkNull_ForModelWithoutHistory()
    {
        await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");

        var result = await CreateController().ListRegistered(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var containers = Assert.IsAssignableFrom<List<RegisteredContainerResponse>>(ok.Value);

        var response = Assert.Single(containers);
        var discovered = Assert.Single(response.DiscoveredModels);
        Assert.Equal("model-1", discovered.Id);
        Assert.Null(discovered.LastBenchmark);
    }

    [Fact]
    public async Task ListRegistered_MixedHistory_SomePopulatedSomeNull()
    {
        var (containerA, modelA) = await SeedRegisteredWithModelAsync("reg-a", "model-a", "llama-a");
        var (_, modelB) = await SeedRegisteredWithModelAsync("reg-b", "model-b", "llama-b");
        await _containerRegistry.AddModelMappingAsync(containerA.Id, modelA.Id);
        await _containerRegistry.AddModelMappingAsync(containerA.Id, modelB.Id);
        await _benchmarks.AddAsync("model-a", "pa", 5, 100, 10, "completed", null);

        var result = await CreateController().ListRegistered(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var containers = Assert.IsAssignableFrom<List<RegisteredContainerResponse>>(ok.Value);

        // Both registered containers are listed; reg-a has two models.
        Assert.Equal(2, containers.Count);
        var regA = Assert.Single(containers, c => c.Id == "reg-a");
        var modelAResponse = Assert.Single(regA.DiscoveredModels, m => m.Id == "model-a");
        var modelBResponse = Assert.Single(regA.DiscoveredModels, m => m.Id == "model-b");
        Assert.NotNull(modelAResponse.LastBenchmark);
        Assert.Equal(5, modelAResponse.LastBenchmark!.TokensPerSec);
        Assert.Null(modelBResponse.LastBenchmark);
    }

    [Fact]
    public async Task GetRegistered_PopulatesLastBenchmark_ForModelWithHistory()
    {
        var (_, model) = await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");
        await _benchmarks.AddAsync("model-1", "p1", 9.5, 250, 20, "completed", null);

        var result = await CreateController().GetRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredContainerResponse>(ok.Value);

        var discovered = Assert.Single(response.DiscoveredModels);
        Assert.Equal("model-1", discovered.Id);
        Assert.NotNull(discovered.LastBenchmark);
        Assert.Equal(9.5, discovered.LastBenchmark!.TokensPerSec);
        Assert.Equal(250, discovered.LastBenchmark.LatencyMs);
    }

    [Fact]
    public async Task GetRegistered_LeavesLastBenchmarkNull_ForModelWithoutHistory()
    {
        await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");

        var result = await CreateController().GetRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredContainerResponse>(ok.Value);

        var discovered = Assert.Single(response.DiscoveredModels);
        Assert.Equal("model-1", discovered.Id);
        Assert.Null(discovered.LastBenchmark);
    }

    [Fact]
    public async Task GetRegistered_ReturnsNotFound_WhenMissing()
    {
        var result = await CreateController().GetRegistered("nope", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetRegistered_UsesLatestBenchmark_PerModel()
    {
        var (_, model) = await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");
        await _benchmarks.AddAsync("model-1", "old", 2, 50, 4, "completed", null);
        await _benchmarks.AddAsync("model-1", "new", 18, 400, 36, "completed", null);

        var result = await CreateController().GetRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredContainerResponse>(ok.Value);

        var discovered = Assert.Single(response.DiscoveredModels);
        Assert.NotNull(discovered.LastBenchmark);
        Assert.Equal(18, discovered.LastBenchmark!.TokensPerSec);
        Assert.Equal(400, discovered.LastBenchmark.LatencyMs);
    }
}
