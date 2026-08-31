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

    private static RegisteredRuntime MakeContainer(string id, string image = "test:latest") => new()
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

    private async Task<(RegisteredRuntime Container, ModelDefinition Model)> SeedRegisteredWithModelAsync(
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
        var containers = Assert.IsAssignableFrom<List<RegisteredRuntimeResponse>>(ok.Value);

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
        var containers = Assert.IsAssignableFrom<List<RegisteredRuntimeResponse>>(ok.Value);

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
        var containers = Assert.IsAssignableFrom<List<RegisteredRuntimeResponse>>(ok.Value);

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
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);

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
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);

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
    public async Task GetRegistered_EmitsLowercaseStatus()
    {
        await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");

        var result = await CreateController().GetRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        // Wire contract: status is lowercase ("ready"), not PascalCase ("Ready").
        Assert.Equal("ready", response.Status);
    }

    [Fact]
    public async Task GetRegistered_UsesLatestBenchmark_PerModel()
    {
        var (_, model) = await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");
        await _benchmarks.AddAsync("model-1", "old", 2, 50, 4, "completed", null);
        await _benchmarks.AddAsync("model-1", "new", 18, 400, 36, "completed", null);

        var result = await CreateController().GetRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);

        var discovered = Assert.Single(response.DiscoveredModels);
        Assert.NotNull(discovered.LastBenchmark);
        Assert.Equal(18, discovered.LastBenchmark!.TokensPerSec);
        Assert.Equal(400, discovered.LastBenchmark.LatencyMs);
    }

    [Fact]
    public async Task StartRegistered_Returns200_WithDiscoveredModels()
    {
        var (container, model) = await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");
        await _benchmarks.AddAsync("model-1", "p1", 12.5, 300, 25, "completed", null);

        // Scripted StartAsync result: the container flips to Ready with a runtime id.
        _registrationService.StartResult = new RegisteredRuntimeWithModels
        {
            Container = container with
            {
                Status = ContainerRegistrationStatus.Ready,
                RuntimeContainerId = "c1",
                MappedPort = 8081
            },
            DiscoveredModels = [model]
        };

        var result = await CreateController().StartRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);

        Assert.Equal("reg-1", response.Id);
        Assert.Equal("ready", response.Status);
        Assert.Equal("c1", response.RuntimeContainerId);
        Assert.Equal(8081, response.MappedPort);
        // discoveredModels populated with lastBenchmark via BuildRegisteredResponseAsync.
        var discovered = Assert.Single(response.DiscoveredModels);
        Assert.Equal("model-1", discovered.Id);
        Assert.NotNull(discovered.LastBenchmark);
        Assert.Equal(12.5, discovered.LastBenchmark!.TokensPerSec);
    }

    [Fact]
    public async Task StartRegistered_UnknownId_ReturnsNotFound()
    {
        _registrationService.StartException = new KeyNotFoundException("Registered container nope not found");

        var result = await CreateController().StartRegistered("nope", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task StartRegistered_StartFailure_Returns200WithErroredContainer()
    {
        var (container, _) = await SeedRegisteredWithModelAsync("reg-1", "model-1", "llama-3");

        // Scripted StartAsync failure: container persisted as Error with a message,
        // endpoint returns 200 (not an error status) so the swarm refetch shows it.
        _registrationService.StartResult = new RegisteredRuntimeWithModels
        {
            Container = container with
            {
                Status = ContainerRegistrationStatus.Error,
                ErrorMessage = "Connection refused"
            },
            DiscoveredModels = []
        };

        var result = await CreateController().StartRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Equal("error", response.Status);
        Assert.Equal("Connection refused", response.ErrorMessage);
    }

    [Fact]
    public async Task UpdateConcurrency_UpdatesList_ReturnsUpdatedRuntime()
    {
        var container = MakeContainer("reg-1");
        await _containerRegistry.CreateAsync(container);

        var updatedContainer = container with { CanRunAlongWith = ["peer-a", "Peer-B"] };
        _registrationService.UpdateConcurrencyResult = updatedContainer;

        var result = await CreateController().UpdateConcurrency("reg-1",
            new UpdateRuntimeConcurrencyRequestDto { CanRunAlongWith = ["peer-a", "Peer-B"] },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Equal("reg-1", response.Id);
        Assert.Equal(2, response.CanRunAlongWith.Count);
        Assert.Contains("peer-a", response.CanRunAlongWith);
        Assert.Contains("Peer-B", response.CanRunAlongWith);
        Assert.Contains("reg-1", _registrationService.UpdatedConcurrencyIds);
    }

    [Fact]
    public async Task UpdateConcurrency_UnknownId_ReturnsNotFound()
    {
        _registrationService.UpdateConcurrencyReturnsNull = true;

        var result = await CreateController().UpdateConcurrency("nope",
            new UpdateRuntimeConcurrencyRequestDto { CanRunAlongWith = ["peer-a"] },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConcurrency_EmptyArray_ClearsList()
    {
        var container = MakeContainer("reg-1");
        await _containerRegistry.CreateAsync(container);

        var updatedContainer = container with { CanRunAlongWith = [] };
        _registrationService.UpdateConcurrencyResult = updatedContainer;

        var result = await CreateController().UpdateConcurrency("reg-1",
            new UpdateRuntimeConcurrencyRequestDto { CanRunAlongWith = [] },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Empty(response.CanRunAlongWith);
    }

    [Fact]
    public async Task UpdateConcurrency_NullBodyList_TreatedAsEmpty()
    {
        var container = MakeContainer("reg-1");
        await _containerRegistry.CreateAsync(container);

        var updatedContainer = container with { CanRunAlongWith = [] };
        _registrationService.UpdateConcurrencyResult = updatedContainer;

        var result = await CreateController().UpdateConcurrency("reg-1",
            new UpdateRuntimeConcurrencyRequestDto { CanRunAlongWith = null },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Empty(response.CanRunAlongWith);
        // Controller cleaned to empty list before passing to service
        Assert.Contains("reg-1", _registrationService.UpdatedConcurrencyIds);
    }

    [Fact]
    public async Task UpdateConcurrency_TrimDedupeCaseInsensitive()
    {
        var container = MakeContainer("reg-1");
        await _containerRegistry.CreateAsync(container);

        var updatedContainer = container with { CanRunAlongWith = ["peer-a", "peer-b"] };
        _registrationService.UpdateConcurrencyResult = updatedContainer;

        var result = await CreateController().UpdateConcurrency("reg-1",
            new UpdateRuntimeConcurrencyRequestDto
            {
                CanRunAlongWith = ["  Peer-A  ", "", "peer-a", "peer-b", "  ", "PEER-A"]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        // Verify the controller cleaned the list before passing to the service.
        // Trim + drop empties + OrdinalIgnoreCase dedupe → ["Peer-A", "peer-b"]
        Assert.NotNull(_registrationService.LastConcurrencyList);
        Assert.Equal(2, _registrationService.LastConcurrencyList!.Count);
        // First occurrence after trim wins; dedupe is case-insensitive
        Assert.Equal("Peer-A", _registrationService.LastConcurrencyList[0]);
        Assert.Equal("peer-b", _registrationService.LastConcurrencyList[1]);
    }

    // ── Stop endpoint tests ───────────────────────────────────────────

    [Fact]
    public async Task StopRegistered_Returns200_UpdatedRuntime()
    {
        var container = MakeContainer("reg-1");
        await _containerRegistry.CreateAsync(container);

        _registrationService.StopResult = container with
        {
            Status = ContainerRegistrationStatus.Error,
            ErrorMessage = "Stopped by user",
            RuntimeProcessId = null,
            RuntimeContainerId = null
        };

        var result = await CreateController().StopRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Equal("reg-1", response.Id);
        Assert.Equal("error", response.Status);
        Assert.Equal("Stopped by user", response.ErrorMessage);
        Assert.Null(response.RuntimeProcessId);
        Assert.Null(response.RuntimeContainerId);
    }

    [Fact]
    public async Task StopRegistered_UnknownId_ReturnsNotFound()
    {
        _registrationService.StopResult = null;

        var result = await CreateController().StopRegistered("nope", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task StopRegistered_StopPersistedViaFake()
    {
        var container = MakeContainer("reg-1");
        await _containerRegistry.CreateAsync(container);

        _registrationService.StopResult = container with
        {
            Status = ContainerRegistrationStatus.Error,
            ErrorMessage = "Stopped by user"
        };

        await CreateController().StopRegistered("reg-1", CancellationToken.None);

        Assert.Contains("reg-1", _registrationService.StoppedIds);
    }

    [Fact]
    public async Task StopRegistered_ClearsProcessId()
    {
        var container = MakeContainer("reg-1") with { RuntimeProcessId = 42, RuntimeContainerId = "c1" };
        await _containerRegistry.CreateAsync(container);

        _registrationService.StopResult = container with
        {
            Status = ContainerRegistrationStatus.Error,
            ErrorMessage = "Stopped by user",
            RuntimeProcessId = null,
            RuntimeContainerId = null
        };

        var result = await CreateController().StopRegistered("reg-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Null(response.RuntimeProcessId);
        Assert.Null(response.RuntimeContainerId);
    }

    // ── Generic stop/restart endpoints (swarm UI card buttons) ─────────

    [Fact]
    public async Task Stop_StaleRuntimeContainerId_RestartsLiveContainerId()
    {
        // The swarm UI Stop button posts the persisted RuntimeContainerId to
        // /api/containers/{id}/stop. After a container recreation that id is stale;
        // the controller must resolve and stop the LIVE id instead.
        _registrationService.LiveIdMap["old-id"] = "new-id";

        var result = await CreateController().Stop("old-id", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("old-id", _registrationService.ResolvedLiveIds);
        Assert.Equal(["new-id"], _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task Stop_UnknownContainerId_PassesThroughToDocker()
    {
        var result = await CreateController().Stop("some-unregistered-id", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(["some-unregistered-id"], _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task Restart_StaleRuntimeContainerId_RestartsLiveContainerId()
    {
        _registrationService.LiveIdMap["old-id"] = "new-id";

        var result = await CreateController().Restart("old-id", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ContainerResponse>(ok.Value);
        Assert.Equal("new-id", response.Id);
        Assert.Contains("old-id", _registrationService.ResolvedLiveIds);
        Assert.Equal(["new-id"], _docker.RestartedContainerIds);
    }

    // ── DTO completeness tests ────────────────────────────────────────

    [Fact]
    public async Task ToggleConcurrency_ReturnsBothUpdatedRuntimes()
    {
        var containerA = MakeContainer("reg-a", "llama:latest") with { DisplayName = "llama-server" };
        var containerB = MakeContainer("reg-b", "vllm:latest") with { DisplayName = "vllm-server" };
        await _containerRegistry.CreateAsync(containerA);
        await _containerRegistry.CreateAsync(containerB);

        _registrationService.ToggleConcurrencyResult = (
            containerA with { CanRunAlongWith = ["vllm-server"] },
            containerB with { CanRunAlongWith = ["llama-server"] }
        );

        var result = await CreateController().ToggleConcurrency(
            new ToggleConcurrencyRequestDto
            {
                RuntimeAId = "reg-a",
                RuntimeBId = "reg-b",
                CanRunAlongWith = true
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        // Anonymous type → extract via reflection-like approach using Dictionary
        var dict = ok.Value!.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(ok.Value));

        var responseA = Assert.IsType<RegisteredRuntimeResponse>(dict["A"]);
        var responseB = Assert.IsType<RegisteredRuntimeResponse>(dict["B"]);
        Assert.Equal("reg-a", responseA.Id);
        Assert.Equal("reg-b", responseB.Id);
        Assert.Contains("vllm-server", responseA.CanRunAlongWith);
        Assert.Contains("llama-server", responseB.CanRunAlongWith);
        Assert.Single(_registrationService.ToggledConcurrencyPairs);
        Assert.Equal(("reg-a", "reg-b", true), _registrationService.ToggledConcurrencyPairs[0]);
    }

    [Fact]
    public async Task ToggleConcurrency_OneMissing_ReturnsNotFound()
    {
        _registrationService.ToggleConcurrencyReturnsNull = true;

        var result = await CreateController().ToggleConcurrency(
            new ToggleConcurrencyRequestDto
            {
                RuntimeAId = "reg-a",
                RuntimeBId = "nope",
                CanRunAlongWith = true
            },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── DTO completeness tests ────────────────────────────────────────

    [Fact]
    public async Task GetRegistered_ScriptRuntime_ShowsRuntimeKindLauncherPathRuntimeProcessId()
    {
        var container = MakeContainer("reg-script") with
        {
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = "/opt/scripts/model-a.sh",
            RuntimeProcessId = 42,
            MappedPort = 9000
        };
        await _containerRegistry.CreateAsync(container);

        var result = await CreateController().GetRegistered("reg-script", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Equal("script", response.RuntimeKind);
        Assert.Equal("/opt/scripts/model-a.sh", response.LauncherPath);
        Assert.Equal(42, response.RuntimeProcessId);
        Assert.Equal(9000, response.MappedPort);
    }

    [Fact]
    public async Task GetRegistered_ContainerRuntime_ShowsContainerKind()
    {
        var container = MakeContainer("reg-container") with
        {
            RuntimeKind = RuntimeKind.Container,
            RuntimeContainerId = "docker-c1"
        };
        await _containerRegistry.CreateAsync(container);

        var result = await CreateController().GetRegistered("reg-container", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RegisteredRuntimeResponse>(ok.Value);
        Assert.Equal("container", response.RuntimeKind);
        Assert.Null(response.LauncherPath);
        Assert.Null(response.RuntimeProcessId);
        Assert.Equal("docker-c1", response.RuntimeContainerId);
    }
}
