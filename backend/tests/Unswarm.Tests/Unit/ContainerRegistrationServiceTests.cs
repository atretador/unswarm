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
        var settings = new FakeSettingsStore(new Settings { HealthCheckTimeoutSeconds = 120 });

        return new ContainerRegistrationService(
            _registry,
            router ?? _router,
            _healthChecker,
            discoveryService ?? new ModelDiscoveryService(new LoggerFactory().CreateLogger<ModelDiscoveryService>()),
            _modelRegistry,
            _clock,
            _logger,
            settings,
            remoteHealthTimeout,
            remoteHealthPollInterval);
    }

    [Fact]
    public async Task RegisterAsync_Success_CreatesContainerWithRegisteredStatus()
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
        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.RuntimeContainerId);
        Assert.Null(result.Container.MappedPort);
        Assert.Null(result.Container.ErrorMessage);
        Assert.Empty(result.DiscoveredModels);
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
    public async Task RegisterAsync_DockerStartFailure_StillRegistersSuccessfully()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Broken",
            Image = "nonexistent:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.ErrorMessage);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RegisterAsync_HealthTimeout_StillRegisters()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Slow",
            Image = "slow:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.ErrorMessage);
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
        var container = new RegisteredRuntime
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
    public async Task RegisterAsync_DiscoveryTransportFailure_StillRegisters()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "DeadPort",
            Image = "dead:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RediscoverAsync_DiscoveryTransportFailure_SetsErrorStatus_DoesNotThrow()
    {
        // OOM-killed container: MappedPort present but port is dead. Rediscover must
        // set Status=Error + message and return (not throw, not flip back to Ready).
        var container = new RegisteredRuntime
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
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "WithRuntime",
            Image = "test:latest"
        };
        var created = await service.RegisterAsync(request);

        // Registration no longer starts a runtime container
        Assert.Null(created.Container.RuntimeContainerId);

        await service.DeleteAsync(created.Container.Id, deleteModels: false);

        // No container to stop
        Assert.Empty(_docker.StoppedContainerIds);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_RegistersWithRemoteAgentInfo()
    {
        // Remote controller: no mapped port in start result (agent omits it),
        // health probe reports healthy immediately.
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
            Healthy = true
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

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.MappedPort);
        Assert.Empty(result.DiscoveredModels);

        // RegisterAsync no longer starts; it only validates and persists.
        Assert.Empty(_docker.StartedContainerIds);
        Assert.Empty(remote.StartedImages);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_NoPortResolvable_StillRegisters()
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

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.ErrorMessage);
        Assert.Null(result.Container.MappedPort);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_HealthNeverHealthy_StillRegisters()
    {
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = false
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

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

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.ErrorMessage);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_ProbeThrowsOnce_StillRegisters()
    {
        var remote = new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 },
            Healthy = true
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

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_PortZero_Registers()
    {
        var remote = new FakeRemoteDockerController
        {
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

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.MappedPort);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_ImageMatchCaseInsensitive_Registers()
    {
        var remote = new FakeRemoteDockerController
        {
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

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RegisterAsync_Host_RegistersWithoutDiscovery()
    {
        var service = CreateService();
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Host Discovered",
            Image = "test:latest"
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RegisterAsync_RemoteAgent_AmbiguousImageMatch_StillRegisters()
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

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.ErrorMessage);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task StartAsync_Host_StartsContainer_ReturnsReady_WithModelsPreserved()
    {
        _docker.MappedPortOverride = StartDiscoveryServer(
            """{"data":[{"id":"model-1","owned_by":"test"}]}""");
        var service = CreateService();

        // Register first so models are mapped to the container.
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Startable",
            Image = "test:latest"
        };
        var created = await service.RegisterAsync(request);

        var started = await service.StartAsync(created.Container.Id);

        Assert.Equal(ContainerRegistrationStatus.Ready, started.Container.Status);
        Assert.NotNull(started.Container.RuntimeContainerId);
        Assert.NotNull(started.Container.MappedPort);
        // Model discovered from the running container.
        var model = Assert.Single(started.DiscoveredModels);
        Assert.Equal("model-1", model.Id);
        var mappedIds = await _registry.GetModelIdsForContainerAsync(created.Container.Id);
        Assert.Contains("model-1", mappedIds);
    }

    [Fact]
    public async Task StartAsync_Host_StartFailure_SetsErrorStatus()
    {
        _docker.FailStart = true;
        _docker.StartErrorMessage = "Container failed to start";

        var service = CreateService();
        var container = new RegisteredRuntime
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
        var container = new RegisteredRuntime
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
        var container = new RegisteredRuntime
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

    // ── Coexistence gate ──────────────────────────────────────────────────────

    private static RegisteredRuntime MakeRuntime(
        string id,
        string image,
        IReadOnlyList<string>? canRunAlongWith = null,
        string agent = "host") => new()
    {
        Id = id,
        DisplayName = image,
        Image = image,
        Agent = agent,
        CanRunAlongWith = canRunAlongWith ?? [],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task StartAsync_StopsIncompatibleRunningContainer_BeforeStart()
    {
        // Target runs alone (empty allow list): any other running runtime on the
        // same host must be stopped first. The peer deliberately has NO mapped
        // port — compatibility must not depend on ports.
        await _registry.CreateAsync(MakeRuntime("reg-alone", "model-a"));
        await _registry.CreateAsync(MakeRuntime("reg-peer", "model-b"));

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "peer-c1",
                ModelId = "model-b",
                ModelName = "model-b",
                Status = ContainerStatus.Running,
                Port = null
            }
        ];

        _docker.MappedPortOverride = StartDiscoveryServer();
        var service = CreateService();
        var result = await service.StartAsync("reg-alone");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Contains("peer-c1", _docker.StoppedContainerIds);
        // The requested runtime was started after the sweep.
        Assert.Equal(["model-a"], _docker.StartedModels);
    }

    [Fact]
    public async Task StartAsync_PeerInAllowList_KeptRunning()
    {
        // Symmetric consent: BOTH sides must list each other.
        await _registry.CreateAsync(MakeRuntime("reg-social", "model-a", canRunAlongWith: ["model-b"]));
        await _registry.CreateAsync(MakeRuntime("reg-peer", "model-b", canRunAlongWith: ["model-a"]));

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "peer-c1",
                ModelId = "model-b",
                ModelName = "model-b",
                Status = ContainerStatus.Running,
                Port = null
            }
        ];

        _docker.MappedPortOverride = StartDiscoveryServer();
        var service = CreateService();
        var result = await service.StartAsync("reg-social");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Empty(_docker.StoppedContainerIds);
    }

    [Fact]
    public async Task StartAsync_OneDirectionalAllowList_StopsPeer()
    {
        // Only the starting runtime lists the peer; the peer does NOT list it back.
        // Symmetric consent is required, so the running peer must be stopped.
        await _registry.CreateAsync(MakeRuntime("reg-social", "model-a", canRunAlongWith: ["model-b"]));
        await _registry.CreateAsync(MakeRuntime("reg-peer", "model-b"));

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "peer-c1",
                ModelId = "model-b",
                ModelName = "model-b",
                Status = ContainerStatus.Running,
                Port = null
            }
        ];

        _docker.MappedPortOverride = StartDiscoveryServer();
        var service = CreateService();
        var result = await service.StartAsync("reg-social");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Contains("peer-c1", _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task StartAsync_AllowListByDisplayName_Symmetric_KeptRunning()
    {
        // Allow-lists reference DisplayNames while images differ — matching must work
        // on display name too, symmetrically.
        await _registry.CreateAsync(MakeRuntime("reg-a", "img-a", canRunAlongWith: ["Beta"]) with { DisplayName = "Alpha" });
        await _registry.CreateAsync(MakeRuntime("reg-b", "img-b", canRunAlongWith: ["Alpha"]) with { DisplayName = "Beta" });

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "peer-c1",
                ModelId = "img-b",
                ModelName = "img-b",
                Status = ContainerStatus.Running,
                Port = null
            }
        ];

        _docker.MappedPortOverride = StartDiscoveryServer();
        var service = CreateService();
        var result = await service.StartAsync("reg-a");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Empty(_docker.StoppedContainerIds);
    }

    [Fact]
    public async Task StartAsync_IncompatiblePeerOnOtherAgent_NotTouched()
    {
        // The allow list is scoped per agent/host: an incompatible runtime running
        // on a DIFFERENT agent must stay untouched.
        var remote = new FakeRemoteDockerController(); // healthy defaults

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        await _registry.CreateAsync(MakeRuntime("reg-agent", "model-c", agent: "gpu1"));
        await _registry.CreateAsync(MakeRuntime("reg-host-peer", "model-b", agent: "host"));

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "host-peer-c1",
                ModelId = "model-b",
                ModelName = "model-b",
                Status = ContainerStatus.Running,
                Port = 8081
            }
        ];

        var service = CreateService(router: router);
        var result = await service.StartAsync("reg-agent");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Empty(_docker.StoppedContainerIds);
        Assert.Equal(["model-c"], remote.StartedImages);
    }

    // ── Recreated container (same name, new docker id) ────────────────────────

    [Fact]
    public async Task StopAsync_RecreatedContainer_StopsNewId()
    {
        // The user recreated the docker container: same name, NEW id. The registry
        // still holds the stale id. Stop must target the live container found by name.
        await _registry.CreateAsync(MakeRuntime("reg-recreated", "localllama_gemma") with
        {
            RuntimeContainerId = "old-id"
        });

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "new-id",
                ModelId = "localllama_gemma",
                ModelName = "localllama_gemma",
                Status = ContainerStatus.Running
            }
        ];

        var service = CreateService();
        var result = await service.StopAsync("reg-recreated");

        Assert.NotNull(result);
        Assert.Equal(["new-id"], _docker.StoppedContainerIds);
        Assert.DoesNotContain("old-id", _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task StopAsync_NoMatchingContainer_FallsBackToStaleId()
    {
        // No container with the registered name exists anymore — keep the previous
        // behavior (stop by persisted id; controller logs a clear warning).
        await _registry.CreateAsync(MakeRuntime("reg-gone", "localllama_gemma") with
        {
            RuntimeContainerId = "old-id"
        });

        _docker.ListedContainers = [];

        var service = CreateService();
        var result = await service.StopAsync("reg-gone");

        Assert.NotNull(result);
        Assert.Equal(["old-id"], _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task DeleteAsync_RecreatedContainer_StopsNewId()
    {
        await _registry.CreateAsync(MakeRuntime("reg-del", "localllama_gemma") with
        {
            RuntimeContainerId = "old-id"
        });

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "new-id",
                ModelId = "localllama_gemma",
                ModelName = "localllama_gemma",
                Status = ContainerStatus.Stopped
            }
        ];

        var service = CreateService();
        await service.DeleteAsync("reg-del", deleteModels: false);

        Assert.Equal(["new-id"], _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task StartAsync_RecreatedContainer_PersistsNewRuntimeContainerId()
    {
        // Start resolves by name inside the controller and returns the LIVE container
        // id — the registry must converge to it instead of keeping the stale one.
        await _registry.CreateAsync(MakeRuntime("reg-start", "localllama_gemma") with
        {
            RuntimeContainerId = "old-id",
            MappedPort = 1234
        });

        _docker.MappedPortOverride = StartDiscoveryServer();
        var service = CreateService();
        var result = await service.StartAsync("reg-start");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        var startedId = Assert.Single(_docker.StartedContainerIds);
        Assert.NotEqual("old-id", startedId);

        var persisted = await _registry.GetAsync("reg-start");
        Assert.NotNull(persisted);
        Assert.Equal(startedId, persisted!.RuntimeContainerId);
    }

    [Fact]
    public async Task ResolveLiveContainerId_RecreatedContainer_ReturnsNewIdAndPersistsRefresh()
    {
        // Mirrors the swarm UI stop/restart path: the card passes the persisted
        // RuntimeContainerId, which is stale after the docker container was recreated.
        await _registry.CreateAsync(MakeRuntime("reg-ui", "localllama_gemma") with
        {
            RuntimeContainerId = "old-id"
        });

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "new-id",
                ModelId = "localllama_gemma",
                ModelName = "localllama_gemma",
                Status = ContainerStatus.Running
            }
        ];

        var service = CreateService();
        var resolved = await service.ResolveLiveContainerIdAsync("old-id");

        Assert.Equal("new-id", resolved);

        var persisted = await _registry.GetAsync("reg-ui");
        Assert.NotNull(persisted);
        Assert.Equal("new-id", persisted!.RuntimeContainerId);
    }

    [Fact]
    public async Task ResolveLiveContainerId_UnknownId_PassesThrough()
    {
        var service = CreateService();
        var resolved = await service.ResolveLiveContainerIdAsync("some-unregistered-container");

        Assert.Equal("some-unregistered-container", resolved);
    }

    [Fact]
    public async Task ResolveLiveContainerId_NoLiveMatch_PassesThroughStaleId()
    {
        // Registered runtime exists but no container matches by id or name anymore —
        // pass the stale id through so the docker layer logs its clear warning.
        await _registry.CreateAsync(MakeRuntime("reg-gone", "localllama_gemma") with
        {
            RuntimeContainerId = "old-id"
        });
        _docker.ListedContainers = [];

        var service = CreateService();
        var resolved = await service.ResolveLiveContainerIdAsync("old-id");

        Assert.Equal("old-id", resolved);
    }

    [Fact]
    public async Task ResolveLiveContainerId_SameIdStillLive_NoRegistryWriteNeeded()
    {
        await _registry.CreateAsync(MakeRuntime("reg-live", "localllama_gemma") with
        {
            RuntimeContainerId = "live-id"
        });

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "live-id",
                ModelId = "localllama_gemma",
                ModelName = "localllama_gemma",
                Status = ContainerStatus.Running
            }
        ];

        var service = CreateService();
        var resolved = await service.ResolveLiveContainerIdAsync("live-id");

        Assert.Equal("live-id", resolved);
        var persisted = await _registry.GetAsync("reg-live");
        Assert.Equal("live-id", persisted!.RuntimeContainerId);
    }

    // ── HealthCheckAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheckAsync_HealthyRuntime_ReturnsHealthyWithModels()
    {
        _docker.MappedPortOverride = StartDiscoveryServer(
            """{"data":[{"id":"model-hc1","owned_by":"test"}]}""");
        var service = CreateService();

        var container = new RegisteredRuntime
        {
            Id = "reg-hc-ok",
            DisplayName = "HcOk",
            Image = "test:latest",
            MappedPort = _docker.MappedPortOverride,
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        var result = await service.HealthCheckAsync("reg-hc-ok", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ContainerRegistrationStatus.Ready, result!.Status);
        // Model was discovered from the running container.
        var mappedIds = await _registry.GetModelIdsForContainerAsync("reg-hc-ok");
        Assert.Contains("model-hc1", mappedIds);
    }

    [Fact]
    public async Task HealthCheckAsync_UnhealthyRuntime_ReturnsErrorStatus()
    {
        _healthChecker.IsReady = false;

        var service = CreateService();
        var container = new RegisteredRuntime
        {
            Id = "reg-hc-fail",
            DisplayName = "HcFail",
            Image = "test:latest",
            MappedPort = 9999,
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        var result = await service.HealthCheckAsync("reg-hc-fail", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ContainerRegistrationStatus.Error, result!.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Health check failed", result.ErrorMessage);
    }

    [Fact]
    public async Task HealthCheckAsync_UnknownId_ReturnsNull()
    {
        var service = CreateService();

        var result = await service.HealthCheckAsync("nonexistent-id", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task HealthCheckAsync_RemoteAgent_RoutesThroughRemoteController()
    {
        var remote = new FakeRemoteDockerController
        {
            Healthy = true,
            Discovered =
            [
                new DiscoveredModel { ModelId = "remote-model-hc" }
            ]
        };

        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker, ["agent:gpu1"] = remote });

        var service = CreateService(router: router);
        var container = new RegisteredRuntime
        {
            Id = "reg-hc-remote",
            DisplayName = "RemoteHc",
            Image = "vllm-serve",
            ContainerPort = 8000,
            MappedPort = 9090,
            Agent = "gpu1",
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await _registry.CreateAsync(container);

        var result = await service.HealthCheckAsync("reg-hc-remote", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ContainerRegistrationStatus.Ready, result!.Status);
        // Health check went through the remote controller, not the local health checker.
        Assert.Equal([9090], remote.HealthCheckedPorts);
        Assert.Empty(_healthChecker.CheckedPorts);
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
    }
}
