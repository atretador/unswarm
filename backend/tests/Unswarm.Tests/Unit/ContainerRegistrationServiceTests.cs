using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Validation;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ContainerRegistrationServiceTests : IDisposable
{
    private readonly FakeContainerRegistry _registry = new();
    private readonly FakeDockerController _docker = new();
    private readonly FakeDockerControllerRouter _router;
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeModelRegistry _modelRegistry = new();
    private readonly FakeClock _clock = new();
    private readonly ILogger<ContainerRegistrationService> _logger =
        new LoggerFactory().CreateLogger<ContainerRegistrationService>();

    public ContainerRegistrationServiceTests()
    {
        _router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker });
    }

    private ContainerRegistrationService CreateService(
        ModelDiscoveryService? discoveryService = null,
        ModelValidator? validator = null,
        FakeDockerControllerRouter? router = null,
        TimeSpan? remoteHealthTimeout = null,
        TimeSpan? remoteHealthPollInterval = null)
    {
        return new ContainerRegistrationService(
            _registry,
            router ?? _router,
            _healthChecker,
            discoveryService ?? new ModelDiscoveryService(new LoggerFactory().CreateLogger<ModelDiscoveryService>()),
            validator ?? new ModelValidator(new LoggerFactory().CreateLogger<ModelValidator>()),
            _modelRegistry,
            _clock,
            _logger,
            remoteHealthTimeout,
            remoteHealthPollInterval);
    }

    [Fact]
    public async Task RegisterAsync_Success_CreatesContainerAndStarts()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Test Llama",
            Image = "ghcr.io/ollama/ollama:latest",
            ContainerPort = 8080,
            MemoryLimitMb = 4096
        };

        var result = await service.RegisterAsync(request);

        Assert.NotNull(result.Container);
        Assert.Equal("Test Llama", result.Container.DisplayName);
        Assert.Equal("ghcr.io/ollama/ollama:latest", result.Container.Image);
        Assert.Equal(8080, result.Container.ContainerPort);
        Assert.Equal(4096, result.Container.MemoryLimitMb);
        // With fake Docker + health checker, the flow succeeds through to Ready
        // (discovery returns empty because no real server, but status = Ready)
        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.NotNull(result.Container.RuntimeContainerId);
        Assert.NotNull(result.Container.MappedPort);
        // Container was registered
        Assert.Single(_registry.CreatedContainers);
    }

    [Fact]
    public async Task RegisterAsync_DisplayNameDefaultsToImage_WhenEmpty()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            Image = "ghcr.io/ollama/ollama:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal("ghcr.io/ollama/ollama:latest", result.Container.DisplayName);
    }

    [Fact]
    public async Task RegisterAsync_DockerStartFailure_SetsErrorStatus()
    {
        _docker.FailStart = true;
        _docker.StartErrorMessage = "Image not found";

        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Broken",
            Image = "nonexistent:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.Equal("Image not found", result.Container.ErrorMessage);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RegisterAsync_HealthTimeout_SetsErrorStatus()
    {
        _healthChecker.IsReady = false;

        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Slow",
            Image = "slow:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.NotNull(result.Container.ErrorMessage);
        Assert.Contains("Health check timeout", result.Container.ErrorMessage!);
    }

    [Fact]
    public async Task DeleteAsync_RemovesContainerAndMappings()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "ToDelete",
            Image = "test:latest"
        };
        var created = await service.RegisterAsync(request);

        // Manually add a model mapping so we can test cleanup
        var modelId = "model-1";
        await _modelRegistry.CreateAsync(new ModelDefinition
        {
            Id = modelId,
            Name = "test-model",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });
        await _registry.AddModelMappingAsync(created.Container.Id, modelId);

        await service.DeleteAsync(created.Container.Id, deleteModels: true);

        Assert.Single(_registry.DeletedContainerIds);
        Assert.Equal(created.Container.Id, _registry.DeletedContainerIds[0]);
        Assert.Null(await _registry.GetAsync(created.Container.Id));
        // Model was deleted because deleteModels=true
        Assert.Contains(modelId, _modelRegistry.DeletedModelIds);
    }

    [Fact]
    public async Task DeleteAsync_WithoutDeleteModels_DeprecatesModels()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Deprecate",
            Image = "test:latest"
        };
        var created = await service.RegisterAsync(request);

        var modelId = "model-dep";
        await _modelRegistry.CreateAsync(new ModelDefinition
        {
            Id = modelId,
            Name = "deprecate-model",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });
        await _registry.AddModelMappingAsync(created.Container.Id, modelId);

        await service.DeleteAsync(created.Container.Id, deleteModels: false);

        Assert.Single(_registry.DeletedContainerIds);
        // Model should be marked Deprecated, not deleted
        Assert.DoesNotContain(modelId, _modelRegistry.DeletedModelIds);
        var model = await _modelRegistry.GetAsync(modelId);
        Assert.NotNull(model);
        Assert.Equal(ModelStatus.Deprecated, model.Status);
    }

    [Fact]
    public async Task DeleteAsync_NotFound_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync("nonexistent", deleteModels: false));
    }

    [Fact]
    public async Task RediscoverAsync_NotFound_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RediscoverAsync("nonexistent"));
    }

    [Fact]
    public async Task RediscoverAsync_NoMappedPort_Throws()
    {
        var service = CreateService();
        var container = new RegisteredContainer
        {
            Id = "reg-noport",
            DisplayName = "NoPort",
            Image = "test:latest",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RediscoverAsync("reg-noport"));
    }

    [Fact]
    public async Task DeleteAsync_RuntimeContainerStopped()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "WithRuntime",
            Image = "test:latest"
        };
        var created = await service.RegisterAsync(request);

        // The registration flow started a runtime container
        Assert.NotNull(created.Container.RuntimeContainerId);

        await service.DeleteAsync(created.Container.Id, deleteModels: false);

        // Docker stop should have been called with the runtime container id
        Assert.Contains(created.Container.RuntimeContainerId!, _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_HappyPath_RoutesThroughRemoteController()
    {
        // Remote controller: no mapped port in start result (agent omits it),
        // health probe reports healthy immediately, discovery returns models.
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1" },
            ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "remote-c1",
                    ModelId = "vllm-serve",
                    ModelName = "vllm-serve",
                    Status = ContainerStatus.Running,
                    Port = 9090
                }
            ],
            Healthy = true,
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ]
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Llama",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Equal("remote-c1", result.Container.RuntimeContainerId);
        Assert.Equal(9090, result.Container.MappedPort);

        // Remote health check was performed on the resolved mapped port
        Assert.Equal([9090], remote.HealthCheckedPorts);

        // Remote models are created Validating, then flipped to Ready after the
        // smoke inference over the agent succeeds (remote validation).
        var model = Assert.Single(result.DiscoveredModels);
        Assert.Equal("llama-3-8b", model.Id);
        Assert.Equal(ModelStatus.Ready, model.Status);

        // Smoke inference was run through the remote controller on the mapped port
        Assert.Single(remote.InferCalls);
        Assert.Equal(9090, remote.InferCalls[0].Port);

        // Start + discovery went through the remote controller, not the host
        Assert.Empty(_docker.StartedContainerIds);
        Assert.Equal(["vllm-serve"], remote.StartedImages);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_NoPortResolvable_SetsError()
    {
        var remote = new FakeRemoteDockerController
        {
            // Start succeeds but returns no mapped port and listing has no match
            StartResult = new ContainerStartResult { ContainerId = "remote-c1" },
            ListedContainers = []
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router);
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote NoPort",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.Equal("Could not determine mapped port for remote container", result.Container.ErrorMessage);
        Assert.Null(result.Container.MappedPort);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_HealthNeverHealthy_EventuallyErrors()
    {
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = false
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        // Small deadline keeps the test fast; poll interval also small.
        var service = CreateService(
            router: router,
            remoteHealthTimeout: TimeSpan.FromMilliseconds(300),
            remoteHealthPollInterval: TimeSpan.FromMilliseconds(50));

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Slow",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.Contains("health check timed out on agent 'gpu1'", result.Container.ErrorMessage);
        // The health probe was actually polled
        Assert.NotEmpty(remote.HealthCheckedPorts);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_ProbeThrowsOnce_ThenHealthy_Completes()
    {
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = true,
            // First probe throws (transient), subsequent probes are healthy. The poll
            // loop must tolerate the exception and keep going until it succeeds.
            HealthProbeScript = new Queue<HealthProbeStep>(
            [
                new HealthProbeStep { Throw = new InvalidOperationException("transient probe failure") }
            ]),
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ]
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(
            router: router,
            remoteHealthTimeout: TimeSpan.FromSeconds(2),
            remoteHealthPollInterval: TimeSpan.FromMilliseconds(10));

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Flaky",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        // Probe exception tolerated → registration completes Ready
        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Equal(9090, result.Container.MappedPort);
        Assert.True(remote.HealthCheckedPorts.Count >= 2, "expected at least two probes (throw then healthy)");
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_PortZero_ResolvesViaListContainers()
    {
        var remote = new FakeRemoteDockerController
        {
            // Start succeeds but reports mapped port 0 (meaningless) → must be
            // treated as unresolved and resolved via ListContainersAsync.
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 0 },
            ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "remote-c1",
                    ModelId = "vllm-serve",
                    ModelName = "vllm-serve",
                    Status = ContainerStatus.Running,
                    Port = 8081
                }
            ],
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ]
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote ZeroPort",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Equal(8081, result.Container.MappedPort);
        Assert.Equal([8081], remote.HealthCheckedPorts);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_ImageMatchCaseInsensitive()
    {
        var remote = new FakeRemoteDockerController
        {
            // Start omits the mapped port → resolved from listing; the agent reports
            // container names in a different case than the registered image.
            StartResult = new ContainerStartResult { ContainerId = "remote-c1" },
            ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "remote-c1",
                    ModelId = "VLLM-SERVE",
                    ModelName = "VLLM-SERVE",
                    Status = ContainerStatus.Running,
                    Port = 9092
                }
            ],
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ]
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Case",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Equal(9092, result.Container.MappedPort);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_AmbiguousImageMatch_SetsError()
    {
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1" },
            // Two containers share the image name — cannot tell which was just started.
            ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "other-1",
                    ModelId = "vllm-serve",
                    ModelName = "vllm-serve",
                    Status = ContainerStatus.Running,
                    Port = 9101
                },
                new ContainerInfo
                {
                    Id = "other-2",
                    ModelId = "vllm-serve",
                    ModelName = "vllm-serve",
                    Status = ContainerStatus.Running,
                    Port = 9102
                }
            ]
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router);
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Ambiguous",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.Contains(
            "2 containers match image 'vllm-serve' on agent 'gpu1'; cannot determine runtime container",
            result.Container.ErrorMessage);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_SmokeValidationFailure_MarksModelInvalid()
    {
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = true,
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ],
            // Smoke inference fails → model must be Invalid
            InferFunc = (port, body, ct) => throw new InvalidOperationException("smoke inference failed")
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Invalid",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        // Container still reaches Ready (discovery succeeded), model marked Invalid
        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        var model = Assert.Single(result.DiscoveredModels);
        Assert.Equal("llama-3-8b", model.Id);
        Assert.Equal(ModelStatus.Invalid, model.Status);

        // Smoke inference was attempted once on the mapped port
        var inferCall = Assert.Single(remote.InferCalls);
        Assert.Equal(9090, inferCall.Port);
        Assert.Contains("\"max_tokens\":8", inferCall.RequestJson);
        Assert.Contains("llama-3-8b", inferCall.RequestJson);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_SmokeValidationSuccess_MarksModelReady()
    {
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = true,
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ],
            InferResult = """{"id":"smoke","choices":[{"message":{"role":"assistant","content":"hi"}}],"usage":{"completion_tokens":1}}"""
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Ready",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        var model = Assert.Single(result.DiscoveredModels);
        Assert.Equal(ModelStatus.Ready, model.Status);

        var inferCall = Assert.Single(remote.InferCalls);
        Assert.Equal(9090, inferCall.Port);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_ErrorShaped200Body_MarksModelInvalid()
    {
        // D1: a 200-with-{"error":...} body must NOT be treated as validation success.
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = true,
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ],
            InferResult = """{"error":{"message":"model not loaded","type":"server_error"}}"""
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote ErrorBody",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        var model = Assert.Single(result.DiscoveredModels);
        Assert.Equal(ModelStatus.Invalid, model.Status);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_EmptyChoicesBody_MarksModelInvalid()
    {
        // D1: a 200 body with an empty choices array is not evidence of a working model.
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = true,
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" }
            ],
            InferResult = """{"id":"x","choices":[],"usage":{"completion_tokens":0}}"""
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote EmptyChoices",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        var model = Assert.Single(result.DiscoveredModels);
        Assert.Equal(ModelStatus.Invalid, model.Status);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_MultipleModels_ValidatedInParallel()
    {
        // D2a: multiple discovered models validate in parallel (Task.WhenAll), and a
        // mix of Ready/Invalid outcomes is preserved while the container stays Ready.
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = true,
            Discovered =
            [
                new DiscoveredModel { ModelId = "llama-3-8b", OwnedBy = "meta" },
                new DiscoveredModel { ModelId = "mistral-7b", OwnedBy = "mistral" }
            ],
            InferFunc = (port, body, ct) => Task.FromResult(
                body.Contains("llama-3-8b")
                    ? """{"id":"ok","choices":[{"message":{"role":"assistant","content":"hi"}}]}"""
                    : """{"error":{"message":"not loaded"}}""")
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Multi",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Equal(2, result.DiscoveredModels.Count);
        Assert.Contains(result.DiscoveredModels, m => m.Id == "llama-3-8b" && m.Status == ModelStatus.Ready);
        Assert.Contains(result.DiscoveredModels, m => m.Id == "mistral-7b" && m.Status == ModelStatus.Invalid);
        Assert.Equal(2, remote.InferCalls.Count);
    }

    public void Dispose()
    {
    }
}
