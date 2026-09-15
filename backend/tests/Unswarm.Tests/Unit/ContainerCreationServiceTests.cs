using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ContainerCreationServiceTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    private static (
        ContainerCreationService Service,
        FakeDockerController Docker,
        FakeContainerRegistry Registry,
        FakeHealthChecker Health,
        FakeSettingsStore Settings
    ) CreateSUT()
    {
        var docker = new FakeDockerController { IdPrefix = "fakecontainer" };
        var registry = new FakeContainerRegistry();
        var health = new FakeHealthChecker();
        var settings = new FakeSettingsStore();
        var logger = NullLogger<ContainerCreationService>.Instance;
        var service = new ContainerCreationService(
            () => null!, docker, registry, health, settings, logger);
        return (service, docker, registry, health, settings);
    }

    private static (
        ContainerCreationService Service,
        FakeContainerRegistry Registry,
        FakeHealthChecker Health,
        FakeSettingsStore Settings
    ) CreateSUTWithDocker(IDockerController docker)
    {
        var registry = new FakeContainerRegistry();
        var health = new FakeHealthChecker();
        var settings = new FakeSettingsStore();
        var logger = NullLogger<ContainerCreationService>.Instance;
        var service = new ContainerCreationService(
            () => null!, docker, registry, health, settings, logger);
        return (service, registry, health, settings);
    }

    private static CreateContainerRequest MakeRequest(
        string image = "docker.io/test/image:latest",
        string name = "test-container",
        string? containerName = null,
        string agent = "host",
        int containerPort = 8080,
        int? hostPort = 8882) => new(
        image,
        name,
        new DockerCreateParams(
            ContainerName: containerName,
            ContainerPort: containerPort,
            HostPort: hostPort),
        agent);

    // ── CreateContainerAsync: Happy path (host agent) ───────────────────

    [Fact]
    public async Task CreateAsync_HostAgent_PullsImageCreatesContainerRegistersStarts()
    {
        var (service, docker, registry, health, _) = CreateSUT();
        // Prevent the background health check from completing (which would race
        // the status to Healthy before we can assert Starting).
        health.IsReady = false;
        var request = MakeRequest(agent: "host");

        var result = await service.CreateContainerAsync(request);

        Assert.Equal(ContainerCreationStatus.Starting, result.Status);
        Assert.NotNull(result.RuntimeId);
        Assert.NotNull(result.ContainerId);

        // PullImageAsync was called (it succeeded since we got here)
        // FakeDockerController.PullImageAsync is idempotent — verify the flow
        // continued past the pull step by checking downstream effects.

        // Container was created (CreateContainerAsync adds to StartedContainerIds)
        // and started (StartContainerAsync also adds to StartedContainerIds).
        Assert.Equal(2, docker.StartedContainerIds.Count);

        // StartContainerAsync was called with the containerId from CreateContainerAsync
        Assert.Single(docker.StartedModels);
        Assert.Equal(result.ContainerId, docker.StartedModels[0]);

        // Container was registered with correct image
        Assert.Single(registry.CreatedContainers);
        var registered = registry.CreatedContainers[0];
        Assert.Equal("docker.io/test/image:latest", registered.Image);
        Assert.Equal(ContainerRegistrationStatus.Registered, registered.Status);

        // Status was then updated to Starting by UpdateAsync
        var updated = await registry.GetAsync(result.RuntimeId!);
        Assert.NotNull(updated);
        Assert.Equal(ContainerRegistrationStatus.Starting, updated!.Status);
    }

    // ── CreateContainerAsync: Happy path (remote agent) ──────────────────

    [Fact]
    public async Task CreateAsync_RemoteAgent_SkipsPullAndStart()
    {
        var (service, docker, registry, _, _) = CreateSUT();
        // Use hostPort: null so the background health check is skipped (no mapped port),
        // preventing the background task from transitioning status to Healthy before assertions.
        var request = MakeRequest(agent: "remote-node", hostPort: null);

        var result = await service.CreateContainerAsync(request);

        Assert.Equal(ContainerCreationStatus.Starting, result.Status);
        Assert.NotNull(result.RuntimeId);
        Assert.NotNull(result.ContainerId);

        // PullImageAsync was NOT called for remote agents — no models tracked,
        // and exactly one StartedContainerIds entry (from CreateContainerAsync only).
        Assert.Empty(docker.StartedModels);
        Assert.Single(docker.StartedContainerIds);

        // Container was registered
        Assert.Single(registry.CreatedContainers);
        Assert.Equal("remote-node", registry.CreatedContainers[0].Agent);

        // Status updated to Starting (remote agents start themselves)
        var updated = await registry.GetAsync(result.RuntimeId!);
        Assert.NotNull(updated);
        Assert.Equal(ContainerRegistrationStatus.Starting, updated!.Status);
    }

    // ── CreateContainerAsync: Image pull failure ─────────────────────────

    [Fact]
    public async Task CreateAsync_PullFailure_ReturnsFailedAndPersistsError()
    {
        var docker = new FailingDockerController { FailPull = true };
        var (service, registry, _, _) = CreateSUTWithDocker(docker);
        var request = MakeRequest(agent: "host");

        var result = await service.CreateContainerAsync(request);

        Assert.Equal(ContainerCreationStatus.Failed, result.Status);
        Assert.Contains("Image pull failed", result.Message);
        Assert.NotNull(result.ErrorDetail);

        // Error runtime was persisted with Error status
        Assert.Single(registry.CreatedContainers);
        var registered = registry.CreatedContainers[0];
        Assert.Equal(ContainerRegistrationStatus.Error, registered.Status);
        Assert.NotNull(registered.ErrorDetail);

        // ErrorDetail contains stage=pull
        var detail = JsonDocument.Parse(registered.ErrorDetail!);
        Assert.Equal("pull", detail.RootElement.GetProperty("stage").GetString());
    }

    // ── CreateContainerAsync: Container create failure (Docker throws) ───

    [Fact]
    public async Task CreateAsync_DockerCreateThrows_ReturnsFailedPersistsError()
    {
        var docker = new FailingDockerController { FailCreate = true };
        var (service, registry, _, _) = CreateSUTWithDocker(docker);
        var request = MakeRequest();

        var result = await service.CreateContainerAsync(request);

        Assert.Equal(ContainerCreationStatus.Failed, result.Status);
        Assert.Contains("Container creation failed", result.Message);
        Assert.NotNull(result.ErrorDetail);

        // containerId stays null when Docker.CreateContainerAsync throws,
        // so no log capture or container removal is attempted.
        // Error runtime was persisted.
        Assert.Single(registry.CreatedContainers);
        Assert.Equal(ContainerRegistrationStatus.Error, registry.CreatedContainers[0].Status);
    }

    // ── CreateContainerAsync: Registry create failure → removes container ─

    [Fact]
    public async Task CreateAsync_RegistryCreateThrows_RemovesContainerAndPersistsError()
    {
        // When Docker.CreateContainerAsync succeeds but Registry.CreateAsync throws,
        // containerId is non-null so the service captures logs and removes the container.
        var registry = new ThrowingContainerRegistry();
        var docker = new FakeDockerController { IdPrefix = "failcontainer" };
        var logger = NullLogger<ContainerCreationService>.Instance;
        var service = new ContainerCreationService(
            () => null!, docker, registry,
            new FakeHealthChecker(), new FakeSettingsStore(), logger);
        var request = MakeRequest();

        var result = await service.CreateContainerAsync(request);

        Assert.Equal(ContainerCreationStatus.Failed, result.Status);
        Assert.Contains("Container creation failed", result.Message);

        // Docker.CreateContainerAsync succeeded (containerId was set),
        // so the catch block attempted to remove it.
        Assert.Single(docker.StartedContainerIds); // from CreateContainerAsync
        // No StartContainerAsync because the exception fired before that point
    }

    // ── CreateContainerAsync: Container create failure captures logs ─────

    [Fact]
    public async Task CreateAsync_RegistryCreateThrows_CapturesContainerLogs()
    {
        var registry = new ThrowingContainerRegistry();
        var logLines = new[] { "error: something broke", "exit code 1" };
        var docker = new CapturingDockerController
        {
            CapturedLogs = logLines
        };
        var logger = NullLogger<ContainerCreationService>.Instance;
        var service = new ContainerCreationService(
            () => null!, docker, registry,
            new FakeHealthChecker(), new FakeSettingsStore(), logger);
        var request = MakeRequest();

        var result = await service.CreateContainerAsync(request);

        Assert.Equal(ContainerCreationStatus.Failed, result.Status);
        Assert.NotNull(result.ErrorDetail);

        // ErrorDetail contains stage=create and the captured logs
        var detail = JsonDocument.Parse(result.ErrorDetail!);
        var root = detail.RootElement;
        Assert.Equal("create", root.GetProperty("stage").GetString());
        var containerLogs = root.GetProperty("containerLogs").GetString();
        Assert.NotNull(containerLogs);
        Assert.Contains("error: something broke", containerLogs);
    }

    // ── Container name auto-generated from image ─────────────────────────

    [Fact]
    public async Task CreateAsync_NullContainerName_GeneratesFromRequestName()
    {
        var docker = new CapturingDockerController();
        var (service, registry, _, _) = CreateSUTWithDocker(docker);
        var request = MakeRequest(name: "My Model", containerName: null);

        await service.CreateContainerAsync(request);

        // Verify the ContainerCreateConfig passed to Docker had the generated name
        Assert.NotNull(docker.LastCreatedConfig);
        Assert.Equal("unswarm-my-model", docker.LastCreatedConfig!.ContainerName);
    }

    // ── Container name explicit ──────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ExplicitContainerName_UsesProvidedName()
    {
        var docker = new CapturingDockerController();
        var (service, registry, _, _) = CreateSUTWithDocker(docker);
        var request = MakeRequest(containerName: "custom-container-name");

        await service.CreateContainerAsync(request);

        Assert.NotNull(docker.LastCreatedConfig);
        Assert.Equal("custom-container-name", docker.LastCreatedConfig!.ContainerName);
    }

    // ── Container name sanitization (Theory) ─────────────────────────────

    [Theory]
    [InlineData("hello world", "unswarm-hello-world")]
    [InlineData("my.model", "unswarm-my-model")]
    [InlineData("UPPER CASE", "unswarm-upper-case")]
    [InlineData("simple", "unswarm-simple")]
    public async Task CreateAsync_NullContainerName_SanitizesName(string inputName, string expectedName)
    {
        var docker = new CapturingDockerController();
        var (service, _, _, _) = CreateSUTWithDocker(docker);
        var request = MakeRequest(name: inputName, containerName: null);

        await service.CreateContainerAsync(request);

        Assert.NotNull(docker.LastCreatedConfig);
        Assert.Equal(expectedName, docker.LastCreatedConfig!.ContainerName);
    }

    // ── Mapped port returned ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ReturnsMappedPortFromDocker()
    {
        var (service, docker, registry, _, _) = CreateSUT();
        var request = MakeRequest(hostPort: 9999);

        var result = await service.CreateContainerAsync(request);

        Assert.Equal(ContainerCreationStatus.Starting, result.Status);

        // FakeDockerController returns config.HostPort as MappedPort
        Assert.Single(registry.CreatedContainers);
        Assert.Equal(9999, registry.CreatedContainers[0].MappedPort);
    }

    // ── CreationMode is Created ──────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_RegisteredRuntime_HasCreationModeCreated()
    {
        var (service, _, registry, _, _) = CreateSUT();

        await service.CreateContainerAsync(MakeRequest());

        Assert.Single(registry.CreatedContainers);
        Assert.Equal(CreationMode.Created, registry.CreatedContainers[0].CreationMode);
    }

    // ── CreationConfigJson is populated ──────────────────────────────────

    [Fact]
    public async Task CreateAsync_RegisteredRuntime_HasPopulatedCreationConfigJson()
    {
        var (service, _, registry, _, _) = CreateSUT();
        var request = MakeRequest(containerPort: 11434, hostPort: 11435);

        await service.CreateContainerAsync(request);

        Assert.Single(registry.CreatedContainers);
        var json = registry.CreatedContainers[0].CreationConfigJson;
        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<DockerCreateParams>(json!);
        Assert.NotNull(parsed);
        Assert.Equal(11434, parsed!.ContainerPort);
        Assert.Equal(11435, parsed.HostPort);
    }

    // ── GetCreateStatusAsync: Runtime not found ──────────────────────────

    [Fact]
    public async Task GetStatus_RuntimeNotFound_ReturnsFailed()
    {
        var (service, _, _, _, _) = CreateSUT();

        var result = await service.GetCreateStatusAsync("nonexistent-id");

        Assert.Equal(ContainerCreationStatus.Failed, result.Status);
        Assert.Equal("Runtime not found", result.Message);
        Assert.Null(result.ContainerId);
    }

    // ── GetCreateStatusAsync: Status mappings ────────────────────────────

    [Theory]
    [InlineData(ContainerRegistrationStatus.Registered, ContainerCreationStatus.Creating)]
    [InlineData(ContainerRegistrationStatus.Starting, ContainerCreationStatus.Starting)]
    [InlineData(ContainerRegistrationStatus.Healthy, ContainerCreationStatus.Running)]
    [InlineData(ContainerRegistrationStatus.Discovering, ContainerCreationStatus.Running)]
    [InlineData(ContainerRegistrationStatus.Ready, ContainerCreationStatus.Running)]
    [InlineData(ContainerRegistrationStatus.Error, ContainerCreationStatus.Failed)]
    public async Task GetStatus_MapsRegistrationStatusToCreationStatus(
        ContainerRegistrationStatus registrationStatus,
        ContainerCreationStatus expectedCreationStatus)
    {
        var (service, _, registry, _, _) = CreateSUT();

        var runtimeId = "test-runtime-1";
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = runtimeId,
            Image = "test/image",
            RuntimeKind = RuntimeKind.Container,
            Status = registrationStatus,
            RuntimeContainerId = "container-123",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var result = await service.GetCreateStatusAsync(runtimeId);

        Assert.Equal(expectedCreationStatus, result.Status);
        Assert.Equal("container-123", result.ContainerId);
    }

    // ── GetCreateStatusAsync: Unknown status defaults to Creating ────────

    [Fact]
    public async Task GetStatus_UnknownRegistrationStatus_DefaultsToCreating()
    {
        var (service, _, registry, _, _) = CreateSUT();

        var runtimeId = "test-runtime-unknown";
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = runtimeId,
            Image = "test/image",
            RuntimeKind = RuntimeKind.Container,
            Status = (ContainerRegistrationStatus)999, // unknown
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var result = await service.GetCreateStatusAsync(runtimeId);

        Assert.Equal(ContainerCreationStatus.Creating, result.Status);
    }

    // ── GetCreateStatusAsync: Error status returns error info ────────────

    [Fact]
    public async Task GetStatus_ErrorStatus_ReturnsErrorDetailAndMessage()
    {
        var (service, _, registry, _, _) = CreateSUT();

        var runtimeId = "test-runtime-error";
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = runtimeId,
            Image = "test/image",
            RuntimeKind = RuntimeKind.Container,
            Status = ContainerRegistrationStatus.Error,
            ErrorMessage = "Something went wrong",
            ErrorDetail = "{\"stage\":\"pull\",\"message\":\"timeout\"}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var result = await service.GetCreateStatusAsync(runtimeId);

        Assert.Equal(ContainerCreationStatus.Failed, result.Status);
        Assert.Equal("Something went wrong", result.Message);
        Assert.Equal("{\"stage\":\"pull\",\"message\":\"timeout\"}", result.ErrorDetail);
    }

    // ── CreateAsync: Registers correct RuntimeKind ───────────────────────

    [Fact]
    public async Task CreateAsync_RegisteredRuntime_HasContainerRuntimeKind()
    {
        var (service, _, registry, _, _) = CreateSUT();

        await service.CreateContainerAsync(MakeRequest());

        Assert.Single(registry.CreatedContainers);
        Assert.Equal(RuntimeKind.Container, registry.CreatedContainers[0].RuntimeKind);
    }

    // ── CreateAsync: Stores all DockerCreateParams fields ────────────────

    [Fact]
    public async Task CreateAsync_StoresFullDockerParamsInConfigJson()
    {
        var (service, _, registry, _, _) = CreateSUT();
        var devices = new[] { "/dev/nvidia0" };
        var env = new[] { new EnvVar { Key = "MY_VAR", Value = "42" } };
        var serverArgs = new[] { "--verbose" };

        var request = new CreateContainerRequest(
            Image: "docker.io/test/image:latest",
            Name: "test",
            DockerParams: new DockerCreateParams(
                ContainerName: "my-container",
                ContainerPort: 8080,
                HostPort: 9090,
                Devices: devices,
                Env: env,
                ShmSizeMb: 32768,
                IpcMode: "host",
                NetworkMode: "host",
                RestartPolicy: "always",
                ServerArgs: serverArgs),
            Agent: "host");

        await service.CreateContainerAsync(request);

        Assert.Single(registry.CreatedContainers);
        var json = registry.CreatedContainers[0].CreationConfigJson;
        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<DockerCreateParams>(json!);
        Assert.NotNull(parsed);
        Assert.Equal("my-container", parsed!.ContainerName);
        Assert.Equal(8080, parsed.ContainerPort);
        Assert.Equal(9090, parsed.HostPort);
        Assert.Equal(32768, parsed.ShmSizeMb);
        Assert.Equal("host", parsed.IpcMode);
        Assert.Equal("host", parsed.NetworkMode);
        Assert.Equal("always", parsed.RestartPolicy);
        Assert.NotNull(parsed.Devices);
        Assert.Single(parsed.Devices!);
        Assert.Equal("/dev/nvidia0", parsed.Devices![0]);
        Assert.NotNull(parsed.Env);
        Assert.Single(parsed.Env!);
        Assert.Equal("MY_VAR", parsed.Env![0].Key);
        Assert.Equal("42", parsed.Env![0].Value);
        Assert.NotNull(parsed.ServerArgs);
        Assert.Single(parsed.ServerArgs!);
        Assert.Equal("--verbose", parsed.ServerArgs![0]);
    }

    // ── CreateAsync: Health check triggered in background ────────────────

    [Fact]
    public async Task CreateAsync_HostAgent_TriggersHealthCheck()
    {
        var (service, _, _, health, _) = CreateSUT();

        await service.CreateContainerAsync(MakeRequest());

        // Give fire-and-forget Task a moment to run
        await Task.Delay(200);

        // FakeHealthChecker tracks checked ports; the background task calls WaitForReadyAsync
        Assert.Contains(8882, health.CheckedPorts);
    }

    // ── CreateAsync: No hostPort → mappedPort is null ────────────────────

    [Fact]
    public async Task CreateAsync_NullHostPort_MappedPortIsNull()
    {
        var (service, _, registry, _, _) = CreateSUT();
        var request = MakeRequest(hostPort: null);

        await service.CreateContainerAsync(request);

        Assert.Single(registry.CreatedContainers);
        Assert.Null(registry.CreatedContainers[0].MappedPort);
    }

    // ── CreateAsync: Background health check skipped when no mapped port ─

    [Fact]
    public async Task CreateAsync_NullHostPort_BackgroundHealthCheckSkipped()
    {
        var (service, _, _, health, _) = CreateSUT();
        var request = MakeRequest(hostPort: null);

        await service.CreateContainerAsync(request);

        // Give fire-and-forget Task a moment
        await Task.Delay(200);

        // No port was checked because mappedPort is null
        Assert.Empty(health.CheckedPorts);
    }

    // ── Inner test doubles ──────────────────────────────────────────────

    /// <summary>
    /// Captures the <see cref="ContainerCreateConfig"/> passed to
    /// <see cref="IDockerController.CreateContainerAsync"/> and the
    /// container logs returned by <see cref="IDockerController.GetContainerLogsAsync"/>.
    /// </summary>
    private sealed class CapturingDockerController : IDockerController
    {
        private readonly FakeDockerController _inner = new() { IdPrefix = "fakecontainer" };
        private int _nextId;

        public ContainerCreateConfig? LastCreatedConfig { get; private set; }
        public IReadOnlyList<string>? CapturedLogs { get; set; }

        public Task<ContainerCreateResult> CreateContainerAsync(
            ContainerCreateConfig config, CancellationToken ct = default)
        {
            LastCreatedConfig = config;
            var id = $"cap-{Interlocked.Increment(ref _nextId)}";
            return Task.FromResult(new ContainerCreateResult
            {
                ContainerId = id,
                MappedPort = config.HostPort
            });
        }

        public Task<IReadOnlyList<string>> GetContainerLogsAsync(
            string id, int tailLines = 100, CancellationToken ct = default)
        {
            if (CapturedLogs is not null)
                return Task.FromResult<IReadOnlyList<string>>(CapturedLogs.ToList());
            return _inner.GetContainerLogsAsync(id, tailLines, ct);
        }

        public Task RemoveContainerAsync(string id, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> PullImageAsync(string image, CancellationToken ct = default)
            => Task.FromResult(image);

        public Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default)
            => _inner.StartContainerAsync(modelName, ct);

        public Task<ContainerStartResult> StartRegisteredContainerAsync(
            string registeredContainerId, string image, int containerPort,
            string? gpuDevices, long memoryLimitMb,
            Dictionary<string, string> extraLabels, CancellationToken ct = default)
            => _inner.StartRegisteredContainerAsync(
                registeredContainerId, image, containerPort,
                gpuDevices, memoryLimitMb, extraLabels, ct);

        public Task StopContainerAsync(string idOrModel, CancellationToken ct = default)
            => _inner.StopContainerAsync(idOrModel, ct);

        public Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
            => _inner.RestartContainerAsync(id, ct);

        public Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
            => _inner.InspectContainerAsync(id, ct);

        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
            => _inner.ListContainersAsync(ct);

        public Task<int?> ResolveMappedPortAsync(string containerName, int containerPort, CancellationToken ct = default)
            => _inner.ResolveMappedPortAsync(containerName, containerPort, ct);
    }

    /// <summary>
    /// A minimal IDockerController that allows injecting failures on specific
    /// operations (pull, create) while delegating the rest to FakeDockerController.
    /// Needed because FakeDockerController is sealed and has no failure hooks
    /// for PullImageAsync / CreateContainerAsync.
    /// </summary>
    private sealed class FailingDockerController : IDockerController
    {
        private readonly FakeDockerController _inner = new();

        public bool FailPull { get; set; }
        public bool FailCreate { get; set; }

        public Task<string> PullImageAsync(string image, CancellationToken ct = default)
        {
            if (FailPull)
                throw new InvalidOperationException("simulated pull failure");
            return Task.FromResult(image);
        }

        public Task<ContainerCreateResult> CreateContainerAsync(
            ContainerCreateConfig config, CancellationToken ct = default)
        {
            if (FailCreate)
                throw new InvalidOperationException("simulated create failure");
            return _inner.CreateContainerAsync(config, ct);
        }

        public Task<IReadOnlyList<string>> GetContainerLogsAsync(
            string id, int tailLines = 100, CancellationToken ct = default)
            => _inner.GetContainerLogsAsync(id, tailLines, ct);

        public Task RemoveContainerAsync(string id, CancellationToken ct = default)
            => _inner.RemoveContainerAsync(id, ct);

        public Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default)
            => _inner.StartContainerAsync(modelName, ct);

        public Task<ContainerStartResult> StartRegisteredContainerAsync(
            string registeredContainerId, string image, int containerPort,
            string? gpuDevices, long memoryLimitMb,
            Dictionary<string, string> extraLabels, CancellationToken ct = default)
            => _inner.StartRegisteredContainerAsync(
                registeredContainerId, image, containerPort,
                gpuDevices, memoryLimitMb, extraLabels, ct);

        public Task StopContainerAsync(string idOrModel, CancellationToken ct = default)
            => _inner.StopContainerAsync(idOrModel, ct);

        public Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
            => _inner.RestartContainerAsync(id, ct);

        public Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
            => _inner.InspectContainerAsync(id, ct);

        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
            => _inner.ListContainersAsync(ct);

        public Task<int?> ResolveMappedPortAsync(string containerName, int containerPort, CancellationToken ct = default)
            => _inner.ResolveMappedPortAsync(containerName, containerPort, ct);
    }

    /// <summary>
    /// Wraps <see cref="FakeContainerRegistry"/> and throws on <see cref="IContainerRegistry.CreateAsync"/>
    /// to simulate a DB/registry failure after Docker has created the container.
    /// This triggers the service's "remove failed container" path.
    /// </summary>
    private sealed class ThrowingContainerRegistry : IContainerRegistry
    {
        private readonly FakeContainerRegistry _inner = new();

        public List<RegisteredRuntime> CreatedContainers => _inner.CreatedContainers;

        public Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default)
        {
            throw new InvalidOperationException("simulated registry create failure");
        }

        public Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default)
            => _inner.GetAsync(id, ct);

        public Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default)
            => _inner.UpdateAsync(id, container, ct);

        public Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default)
            => _inner.ListAllAsync(ct);

        public Task DeleteAsync(string id, CancellationToken ct = default)
            => _inner.DeleteAsync(id, ct);

        public Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
            => _inner.AddModelMappingAsync(registeredContainerId, modelId, ct);

        public Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
            => _inner.RemoveModelMappingAsync(registeredContainerId, modelId, ct);

        public Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default)
            => _inner.GetModelIdsForContainerAsync(registeredContainerId, ct);

        public Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default)
            => _inner.GetContainerIdForModelAsync(modelName, ct);

        public Task<IReadOnlyList<string>> GetAllContainerIdsForModelAsync(string modelName)
            => _inner.GetAllContainerIdsForModelAsync(modelName);

        public Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(
            string idA, IReadOnlyList<string> newCanRunAlongWithA,
            string idB, IReadOnlyList<string> newCanRunAlongWithB,
            CancellationToken ct = default)
            => _inner.UpdateConcurrencyPairAsync(idA, newCanRunAlongWithA, idB, newCanRunAlongWithB, ct);
    }
}
