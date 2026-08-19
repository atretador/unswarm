using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
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
    private readonly List<TcpListener> _listeners = [];

    public ContainerRegistrationServiceTests()
    {
        _router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker });
    }

    /// <summary>
    /// Starts a local HTTP listener serving the given body so host-path discovery
    /// (which now throws on transport failure) succeeds with a real endpoint.
    /// </summary>
    private int StartDiscoveryServer(string jsonResponse = """{"data":[]}""")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);
                    using var writer = new StreamWriter(stream) { AutoFlush = true };

                    await reader.ReadLineAsync();
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && line.Length > 0) { }

                    var bodyBytes = Encoding.UTF8.GetBytes(jsonResponse);
                    await writer.WriteAsync(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\n\r\n");
                    await stream.WriteAsync(bodyBytes);
                }
            }
            catch { /* listener stopped */ }
        });

        return port;
    }

    private ContainerRegistrationService CreateService(
        ModelDiscoveryService? discoveryService = null,
        FakeDockerControllerRouter? router = null,
        TimeSpan? remoteHealthTimeout = null,
        TimeSpan? remoteHealthPollInterval = null)
    {
        return new ContainerRegistrationService(
            _registry,
            router ?? _router,
            _healthChecker,
            discoveryService ?? new ModelDiscoveryService(new LoggerFactory().CreateLogger<ModelDiscoveryService>()),
            _modelRegistry,
            _clock,
            _logger,
            remoteHealthTimeout,
            remoteHealthPollInterval);
    }

    [Fact]
    public async Task RegisterAsync_Success_CreatesContainerAndStarts()
    {
        _docker.MappedPortOverride = StartDiscoveryServer();
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
        // With fake Docker + health checker + a real discovery endpoint, the flow
        // succeeds through to Ready (discovery returns zero models).
        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.NotNull(result.Container.RuntimeContainerId);
        Assert.NotNull(result.Container.MappedPort);
        // Container was registered
        Assert.Single(_registry.CreatedContainers);
    }

    [Fact]
    public async Task RegisterAsync_DisplayNameDefaultsToImage_WhenEmpty()
    {
        _docker.MappedPortOverride = StartDiscoveryServer();
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
        _docker.MappedPortOverride = StartDiscoveryServer();
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
        _docker.MappedPortOverride = StartDiscoveryServer();
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
    public async Task RegisterAsync_DiscoveryTransportFailure_SetsErrorStatus()
    {
        // Dead port (no listener) → ModelDiscoveryService throws → register must
        // surface the real error instead of silently going Ready.
        _docker.MappedPortOverride = 1; // almost certainly nothing listening
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "DeadPort",
            Image = "dead:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.NotNull(result.Container.ErrorMessage);
        // The transport failure (connection refused) surfaces, not a silent Ready.
        Assert.Contains("Connection refused", result.Container.ErrorMessage);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RediscoverAsync_DiscoveryTransportFailure_SetsErrorStatus_DoesNotThrow()
    {
        // OOM-killed container: MappedPort present but port is dead. Rediscover must
        // set Status=Error + message and return (not throw, not flip back to Ready).
        var container = new RegisteredContainer
        {
            Id = "reg-dead",
            DisplayName = "OomKilled",
            Image = "test:latest",
            MappedPort = 1, // dead port
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        var service = CreateService();
        var result = await service.RediscoverAsync("reg-dead");

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.NotNull(result.Container.ErrorMessage);
        Assert.Contains("Model discovery failed", result.Container.ErrorMessage);
        Assert.Empty(result.DiscoveredModels);
        // Still persisted as Error in the registry.
        var persisted = await _registry.GetAsync("reg-dead");
        Assert.Equal(ContainerRegistrationStatus.Error, persisted!.Status);
        Assert.Contains("Model discovery failed", persisted.ErrorMessage!);
    }

    [Fact]
    public async Task DeleteAsync_RuntimeContainerStopped()
    {
        _docker.MappedPortOverride = StartDiscoveryServer();
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

        // Discovery IS validation: the model is Ready immediately, with ZERO smoke
        // inference calls (no chat_completion during registration).
        var model = Assert.Single(result.DiscoveredModels);
        Assert.Equal("llama-3-8b", model.Id);
        Assert.Equal(ModelStatus.Ready, model.Status);
        Assert.Empty(remote.InferCalls);

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
    public async Task RegisterAsync_Host_DiscoveredModel_LandsReady_WithoutSmokeValidation()
    {
        // Discovery IS validation: a host-discovered model is created Ready directly,
        // with no validator/smoke inference call during registration.
        _docker.MappedPortOverride = StartDiscoveryServer(
            """{"data":[{"id":"llama-3-8b","owned_by":"meta"}]}""");

        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Host Discovered",
            Image = "test:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        var model = Assert.Single(result.DiscoveredModels);
        Assert.Equal("llama-3-8b", model.Id);
        Assert.Equal(ModelStatus.Ready, model.Status);

        // The model is persisted Ready in the registry.
        var persisted = await _modelRegistry.GetAsync("llama-3-8b");
        Assert.NotNull(persisted);
        Assert.Equal(ModelStatus.Ready, persisted!.Status);
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
    public async Task StartAsync_Host_StartsContainer_ReturnsReady_WithModelsPreserved()
    {
        _docker.MappedPortOverride = StartDiscoveryServer();
        var service = CreateService();

        // Register first so models are mapped to the container.
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Startable",
            Image = "test:latest"
        };
        var created = await service.RegisterAsync(request);

        // Manually map a model (as the initial registration would).
        var modelId = "model-1";
        await _modelRegistry.CreateAsync(new ModelDefinition
        {
            Id = modelId,
            Name = "start-model",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });
        await _registry.AddModelMappingAsync(created.Container.Id, modelId);

        var started = await service.StartAsync(created.Container.Id);

        Assert.Equal(ContainerRegistrationStatus.Ready, started.Container.Status);
        Assert.NotNull(started.Container.RuntimeContainerId);
        Assert.NotNull(started.Container.MappedPort);
        // Models preserved, no re-discovery: the same mapping still exists.
        var model = Assert.Single(started.DiscoveredModels);
        Assert.Equal(modelId, model.Id);
        var mappedIds = await _registry.GetModelIdsForContainerAsync(created.Container.Id);
        Assert.Equal([modelId], mappedIds);
    }

    [Fact]
    public async Task StartAsync_Host_StartFailure_SetsErrorStatus()
    {
        _docker.FailStart = true;
        _docker.StartErrorMessage = "Container failed to start";

        var service = CreateService();
        var container = new RegisteredContainer
        {
            Id = "reg-start-fail",
            DisplayName = "FailStart",
            Image = "test:latest",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        var result = await service.StartAsync("reg-start-fail");

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.Equal("Container failed to start", result.Container.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_Host_HealthTimeout_SetsErrorStatus()
    {
        _docker.MappedPortOverride = StartDiscoveryServer();
        _healthChecker.IsReady = false;

        var service = CreateService();
        var container = new RegisteredContainer
        {
            Id = "reg-slow",
            DisplayName = "SlowStart",
            Image = "test:latest",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        var result = await service.StartAsync("reg-slow");

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.NotNull(result.Container.ErrorMessage);
        Assert.Contains("Health check timeout", result.Container.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_RemoteAgent_RoutesThroughRouter_AndHealthChecksResolvedPort()
    {
        var remote = new FakeRemoteDockerController
        {
            // Agent omits mapped port in start result → resolved from listing.
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
            Healthy = true
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router, remoteHealthPollInterval: TimeSpan.FromMilliseconds(5));
        var container = new RegisteredContainer
        {
            Id = "reg-remote-start",
            DisplayName = "RemoteStart",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        var result = await service.StartAsync("reg-remote-start");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Equal("remote-c1", result.Container.RuntimeContainerId);
        Assert.Equal(9090, result.Container.MappedPort);
        // Start went through the remote controller, not the host.
        Assert.Empty(_docker.StartedContainerIds);
        Assert.Equal(["vllm-serve"], remote.StartedImages);
        // Health polled on the resolved port.
        Assert.Equal([9090], remote.HealthCheckedPorts);
    }

    [Fact]
    public async Task StartAsync_UnknownId_ThrowsKeyNotFound()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.StartAsync("nonexistent"));
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
    }
}
