using System.Reflection;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Unswarm.Core.Contracts;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Validation;
using Unswarm.Tests.Fakes;
using ContainerStatus = Unswarm.Core.Models.ContainerStatus;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for the real <see cref="DockerController"/> against a hermetic
/// in-process fake Docker Engine (Kestrel on loopback). The sealed controller's
/// private readonly <c>_client</c> field is redirected to a Docker.DotNet client
/// pointed at the fake engine — no Docker socket/daemon is touched.
///
/// NOTE: container ids in these tests are always ≥ 12 chars because production
/// logs truncate them with <c>id[..12]</c>.
/// </summary>
public sealed class DockerControllerTests : IAsyncLifetime
{
    private FakeDockerEngine _engine = null!;
    private FakeDockerEngineState _state = null!;

    public async Task InitializeAsync()
    {
        _state = new FakeDockerEngineState();
        _engine = await FakeDockerEngine.StartAsync(_state);
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    private DockerController CreateController(DockerPolicyOptions? policy = null)
    {
        var controller = new DockerController(
            NullLogger<DockerController>.Instance,
            Options.Create(policy ?? new DockerPolicyOptions()));

        var client = new DockerClientConfiguration(_engine.BaseUri).CreateClient();
        var field = typeof(DockerController).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(controller, client);
        return controller;
    }

    // ── JSON builders ─────────────────────────────────────────────────

    private static string Summary(
        string id,
        string name,
        string state = "running",
        string? registry = null,
        long sizeRw = 0,
        (int Private, int Public)? port = null,
        long created = 1_700_000_000)
    {
        var ports = port is null
            ? "[]"
            : $"[{{\"PrivatePort\":{port.Value.Private},\"PublicPort\":{port.Value.Public},\"Type\":\"tcp\"}}]";
        var labels = registry is null ? "{}" : $"{{\"unswarm.registry\":\"{registry}\"}}";
        return $"{{\"Id\":\"{id}\",\"Names\":[\"/{name}\"],\"State\":\"{state}\",\"Created\":{created}," +
               $"\"SizeRw\":{sizeRw},\"Ports\":{ports},\"Labels\":{labels}}}";
    }

    private static string ListJson(params string[] entries) => "[" + string.Join(",", entries) + "]";

    private static string Inspect(
        string id,
        string status = "running",
        long pid = 4321,
        long memory = 1_073_741_824,
        bool running = true,
        string startedAt = "2026-01-01T00:00:00Z",
        params (int Private, string? Host)[] bindings)
    {
        var ports = bindings.Length == 0
            ? "{}"
            : "{" + string.Join(",", bindings.Select(b => $"\"{b.Private}/tcp\":[{{\"HostPort\":\"{b.Host}\"}}]")) + "}";
        return $"{{\"Id\":\"{id}\",\"State\":{{\"Status\":\"{status}\",\"Running\":{(running ? "true" : "false")}," +
               $"\"Pid\":{pid},\"StartedAt\":\"{startedAt}\"}},\"HostConfig\":{{\"Memory\":{memory}}}," +
               $"\"NetworkSettings\":{{\"Ports\":{ports}}}}}";
    }

    // ── ListContainersAsync ───────────────────────────────────────────

    [Fact]
    public async Task ListContainers_MapsFieldsPortsAndRegistryLabel()
    {
        _state.ContainersListJson = ListJson(
            Summary("id-run-00001", "llama", registry: "reg-1", sizeRw: 10_485_760, port: (8080, 8081)),
            Summary("id-exit-0001", "other", state: "exited", port: (9090, 9091)));
        var controller = CreateController();

        var containers = await controller.ListContainersAsync();

        Assert.Equal(2, containers.Count);
        var first = containers.Single(c => c.Id == "id-run-00001");
        Assert.Equal("llama", first.ModelName);
        Assert.Equal("llama", first.ModelId);
        Assert.Equal(ContainerStatus.Running, first.Status);
        Assert.Equal(8081, first.Port);
        Assert.Equal(10, first.MemoryMb);
        Assert.Equal("reg-1", first.RegisteredRuntimeId);
        Assert.Equal(ContainerStatus.Stopped, containers.Single(c => c.Id == "id-exit-0001").Status);
    }

    [Theory]
    [InlineData("running", ContainerStatus.Running)]
    [InlineData("created", ContainerStatus.Starting)]
    [InlineData("restarting", ContainerStatus.Starting)]
    [InlineData("stopping", ContainerStatus.Stopping)]
    [InlineData("exited", ContainerStatus.Stopped)]
    [InlineData("dead", ContainerStatus.Error)]
    [InlineData("paused", ContainerStatus.Error)]
    [InlineData("weird", ContainerStatus.Error)]
    public async Task ListContainers_MapContainerStatus(string dockerState, ContainerStatus expected)
    {
        _state.ContainersListJson = ListJson(Summary("id-000000001", "c", state: dockerState));
        var controller = CreateController();

        var containers = await controller.ListContainersAsync();

        Assert.Equal(expected, Assert.Single(containers).Status);
    }

    [Fact]
    public async Task ListContainers_NoNameOrPort_FallsBackToIdAndNullPort()
    {
        _state.ContainersListJson = """[{"Id":"abcdef0123456789","Names":[],"State":"running","Created":1700000000,"SizeRw":0,"Ports":[],"Labels":{}}]""";
        var controller = CreateController();

        var container = Assert.Single(await controller.ListContainersAsync());

        Assert.Equal("abcdef012345", container.ModelName); // ID[..12]
        Assert.Null(container.Port);
    }

    // ── StartContainerAsync ───────────────────────────────────────────

    [Fact]
    public async Task StartContainer_RunningContainer_ReturnsMappedPortWithoutStarting()
    {
        _state.ContainersListJson = ListJson(Summary("cid-00000001", "llama", state: "running"));
        _state.InspectJsonById["cid-00000001"] = Inspect("cid-00000001", bindings: (8080, "8081"));
        var controller = CreateController();

        var result = await controller.StartContainerAsync("llama");

        Assert.Equal("cid-00000001", result.ContainerId);
        Assert.Equal(8081, result.MappedPort);
        Assert.Null(result.ErrorMessage);
        Assert.Empty(_state.Started); // already running → not started again
    }

    [Fact]
    public async Task StartContainer_StoppedContainer_StartsThenInspects()
    {
        _state.ContainersListJson = ListJson(Summary("cid-00000002", "llama", state: "exited"));
        _state.InspectJsonById["cid-00000002"] = Inspect("cid-00000002", bindings: (8080, "8082"));
        var controller = CreateController();

        var result = await controller.StartContainerAsync("llama");

        Assert.Equal("cid-00000002", result.ContainerId);
        Assert.Equal(8082, result.MappedPort);
        Assert.Equal(["cid-00000002"], _state.Started);
    }

    [Fact]
    public async Task StartContainer_NotFound_ReturnsErrorMessageNoThrow()
    {
        _state.ContainersListJson = "[]";
        var controller = CreateController();

        var result = await controller.StartContainerAsync("missing");

        Assert.Equal(string.Empty, result.ContainerId);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("missing", result.ErrorMessage);
    }

    [Fact]
    public async Task StartContainer_NonNumericHostPort_MappedPortNull()
    {
        _state.ContainersListJson = ListJson(Summary("cid-00000003", "llama", state: "running"));
        _state.InspectJsonById["cid-00000003"] = Inspect("cid-00000003", bindings: (8080, "not-a-port"));
        var controller = CreateController();

        var result = await controller.StartContainerAsync("llama");

        Assert.Equal("cid-00000003", result.ContainerId);
        Assert.Null(result.MappedPort);
    }

    // ── StartRegisteredContainerAsync ─────────────────────────────────

    [Fact]
    public async Task StartRegistered_AlreadyRunning_NoStartCall()
    {
        _state.ContainersListJson = ListJson(Summary("rcid-0000001", "myimage", state: "running"));
        _state.InspectJsonById["rcid-0000001"] = Inspect("rcid-0000001", bindings: (9000, "9001"));
        var controller = CreateController();

        var result = await controller.StartRegisteredContainerAsync("reg-1", "myimage", 9000, null, 0, []);

        Assert.Equal("rcid-0000001", result.ContainerId);
        Assert.Equal(9001, result.MappedPort);
        Assert.Empty(_state.Started);
    }

    [Fact]
    public async Task StartRegistered_Stopped_StartsContainer()
    {
        _state.ContainersListJson = ListJson(Summary("rcid-0000002", "myimage", state: "created"));
        _state.InspectJsonById["rcid-0000002"] = Inspect("rcid-0000002", bindings: (9000, "9002"));
        var controller = CreateController();

        var result = await controller.StartRegisteredContainerAsync("reg-1", "myimage", 9000, null, 0, []);

        Assert.Equal(9002, result.MappedPort);
        Assert.Equal(["rcid-0000002"], _state.Started);
    }

    [Fact]
    public async Task StartRegistered_NotFound_ReturnsError()
    {
        _state.ContainersListJson = "[]";
        var controller = CreateController();

        var result = await controller.StartRegisteredContainerAsync("reg-1", "ghost", 9000, null, 0, []);

        Assert.Equal(string.Empty, result.ContainerId);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── StopContainerAsync ────────────────────────────────────────────

    [Fact]
    public async Task StopContainer_ByDirectId_StopsResolvedId()
    {
        _state.InspectJsonById["cid-stop-001"] = Inspect("cid-stop-001");
        var controller = CreateController();

        await controller.StopContainerAsync("cid-stop-001");

        Assert.Equal(["cid-stop-001"], _state.Stopped);
    }

    [Fact]
    public async Task StopContainer_ByName_ResolvesViaList()
    {
        // Inspect("llama") 404s (no such key); list provides the name→id mapping.
        _state.ContainersListJson = ListJson(Summary("cid-name-001", "llama", state: "running"));
        _state.InspectJsonById["cid-name-001"] = Inspect("cid-name-001");
        var controller = CreateController();

        await controller.StopContainerAsync("llama");

        Assert.Equal(["cid-name-001"], _state.Stopped);
    }

    [Fact]
    public async Task StopContainer_NoMatch_WarnsAndReturns()
    {
        _state.ContainersListJson = "[]";
        var controller = CreateController();

        await controller.StopContainerAsync("ghost"); // must not throw

        Assert.Empty(_state.Stopped);
    }

    [Fact]
    public async Task StopContainer_ContainerNotFound_IsSwallowed()
    {
        _state.InspectJsonById["cid-gone-001"] = Inspect("cid-gone-001");
        _state.StopStatusCode = 404; // DockerContainerNotFoundException
        var controller = CreateController();

        await controller.StopContainerAsync("cid-gone-001"); // must not throw
    }

    [Fact]
    public async Task StopContainer_GenericFailure_IsSwallowed()
    {
        _state.InspectJsonById["cid-boom-0001"] = Inspect("cid-boom-0001");
        _state.StopStatusCode = 500; // generic DockerApiException → catch(Exception)
        var controller = CreateController();

        await controller.StopContainerAsync("cid-boom-0001"); // must not throw
    }

    // ── RestartContainerAsync ─────────────────────────────────────────

    [Fact]
    public async Task RestartContainer_StopsStartsAndInspects()
    {
        _state.InspectJsonById["cid-r-0000001"] = Inspect("cid-r-0000001", bindings: (8080, "8085"));
        var controller = CreateController();

        var result = await controller.RestartContainerAsync("cid-r-0000001");

        Assert.Equal("cid-r-0000001", result.ContainerId);
        Assert.Equal(8085, result.MappedPort);
        Assert.Contains("cid-r-0000001", _state.Stopped);
        Assert.Contains("cid-r-0000001", _state.Started);
    }

    [Fact]
    public async Task RestartContainer_InspectFailure_ReturnsError()
    {
        // stop/start succeed (engine returns 204), but inspect 404s → error branch.
        var controller = CreateController();

        var result = await controller.RestartContainerAsync("cid-missing01");

        Assert.Equal("cid-missing01", result.ContainerId);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── InspectContainerAsync ─────────────────────────────────────────

    [Fact]
    public async Task InspectContainer_MapsStatePidMemoryAndUptime()
    {
        _state.InspectJsonById["cid-i-0000001"] = Inspect("cid-i-0000001", status: "running", pid: 777,
            memory: 536_870_912, running: true);
        var controller = CreateController();

        var result = await controller.InspectContainerAsync("cid-i-0000001");

        Assert.NotNull(result);
        Assert.Equal("running", result!.Status);
        Assert.Equal(777, result.Pid);
        Assert.Equal(512, result.MemoryMb);
        Assert.True(result.UptimeSeconds > 0);
    }

    [Fact]
    public async Task InspectContainer_NotRunning_UptimeZero()
    {
        _state.InspectJsonById["cid-s-0000001"] = Inspect("cid-s-0000001", status: "exited", running: false);
        var controller = CreateController();

        var result = await controller.InspectContainerAsync("cid-s-0000001");

        Assert.NotNull(result);
        Assert.Equal(0, result!.UptimeSeconds);
    }

    [Fact]
    public async Task InspectContainer_Missing_ReturnsNull()
    {
        var controller = CreateController();

        Assert.Null(await controller.InspectContainerAsync("nope"));
    }

    // ── GetContainerLogsAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetContainerLogs_ReturnsLines()
    {
        _state.LogsBody = "line one\nline two\n";
        var controller = CreateController();

        var lines = await controller.GetContainerLogsAsync("cid-l-0000001", tailLines: 50);

        Assert.Equal(["line one", "line two"], lines);
    }

    // ── RemoveContainerAsync ──────────────────────────────────────────

    [Fact]
    public async Task RemoveContainer_Success()
    {
        var controller = CreateController();

        await controller.RemoveContainerAsync("cid-rm-000001");

        Assert.Equal(["cid-rm-000001"], _state.Removed);
    }

    [Fact]
    public async Task RemoveContainer_NotFound_IsSwallowed()
    {
        _state.RemoveStatusCode = 404;
        var controller = CreateController();

        await controller.RemoveContainerAsync("cid-gone-001"); // must not throw
    }

    [Fact]
    public async Task RemoveContainer_ServerError_IsSwallowed()
    {
        _state.RemoveStatusCode = 500;
        var controller = CreateController();

        await controller.RemoveContainerAsync("cid-err-0001"); // generic catch
    }

    // ── ResolveMappedPortAsync ────────────────────────────────────────

    [Fact]
    public async Task ResolveMappedPort_Match_ReturnsHostPort()
    {
        _state.ContainersListJson = ListJson(Summary("cid-p-0000001", "llama", state: "running"));
        _state.InspectJsonById["cid-p-0000001"] = Inspect("cid-p-0000001", bindings: (8080, "8099"));
        var controller = CreateController();

        Assert.Equal(8099, await controller.ResolveMappedPortAsync("llama", 8080));
    }

    [Fact]
    public async Task ResolveMappedPort_NoMatch_ReturnsNull()
    {
        _state.ContainersListJson = "[]";
        var controller = CreateController();

        Assert.Null(await controller.ResolveMappedPortAsync("ghost", 8080));
    }

    [Fact]
    public async Task ResolveMappedPort_InspectFailure_ReturnsNull()
    {
        _state.ContainersListJson = ListJson(Summary("cid-x-0000001", "llama", state: "running"));
        // No inspect JSON for cid-x → 404 inside try → catch → null.
        var controller = CreateController();

        Assert.Null(await controller.ResolveMappedPortAsync("llama", 8080));
    }

    [Fact]
    public async Task ResolveMappedPort_NoBindingForPort_ReturnsNull()
    {
        _state.ContainersListJson = ListJson(Summary("cid-y-0000001", "llama", state: "running"));
        _state.InspectJsonById["cid-y-0000001"] = Inspect("cid-y-0000001", bindings: (1234, "1235"));
        var controller = CreateController();

        Assert.Null(await controller.ResolveMappedPortAsync("llama", 8080));
    }

    // ── PullImageAsync ────────────────────────────────────────────────

    [Fact]
    public async Task PullImage_AlreadyLocal_DoesNotPull()
    {
        _state.InspectJsonByImage["llama:latest"] = """{"Id":"sha256:abcdef1234567890"}""";
        var controller = CreateController();

        var result = await controller.PullImageAsync("llama:latest");

        Assert.Equal("llama:latest", result);
        Assert.Equal(0, _state.ImageCreateCalls);
    }

    [Fact]
    public async Task PullImage_Missing_PullsImage()
    {
        // No image inspect entry → 404 → CreateImageAsync.
        var controller = CreateController();

        var result = await controller.PullImageAsync("llama:latest");

        Assert.Equal("llama:latest", result);
        Assert.Equal(1, _state.ImageCreateCalls);
    }

    [Fact]
    public async Task PullImage_Missing_ReportsProgressMessages()
    {
        // Non-empty progress stream exercises the Progress<JSONMessage> callback.
        _state.ImageCreateBody = "{\"status\":\"Downloading\"}\n{\"status\":\"Extracting\"}\n";
        var controller = CreateController();

        var result = await controller.PullImageAsync("llama:latest");

        Assert.Equal("llama:latest", result);
        Assert.Equal(1, _state.ImageCreateCalls);
    }

    // ── CreateContainerAsync ──────────────────────────────────────────

    private static ContainerCreateConfig ValidConfig(
        string name = "newone",
        int containerPort = 8080,
        int? hostPort = null,
        string restartPolicy = "unless-stopped",
        string networkMode = "bridge",
        IReadOnlyList<string>? devices = null,
        IReadOnlyList<VolumeMount>? volumes = null,
        IReadOnlyList<EnvVar>? env = null,
        IReadOnlyList<string>? serverArgs = null) => new()
    {
        Image = "llama:latest",
        ContainerName = name,
        ContainerPort = containerPort,
        HostPort = hostPort,
        ShmSizeMb = 16384,
        IpcMode = "private",
        NetworkMode = networkMode,
        RestartPolicy = restartPolicy,
        Devices = devices,
        Volumes = volumes,
        Env = env,
        ServerArgs = serverArgs
    };

    private const string CreatedId = "new-id-000001";

    [Fact]
    public async Task CreateContainer_Valid_ResolvesMappedPort()
    {
        _state.CreateResponseJson = $"{{\"Id\":\"{CreatedId}\",\"Warnings\":[]}}";
        _state.InspectJsonById[CreatedId] = Inspect(CreatedId, bindings: (8080, "8087"));
        var controller = CreateController();

        var result = await controller.CreateContainerAsync(ValidConfig());

        Assert.Equal(CreatedId, result.ContainerId);
        Assert.Equal(8087, result.MappedPort);
        Assert.True(_state.PathRequested("/containers/create"));
    }

    [Fact]
    public async Task CreateContainer_ExplicitHostPort_EncodedInRequest()
    {
        _state.CreateResponseJson = $"{{\"Id\":\"{CreatedId}\",\"Warnings\":[]}}";
        var controller = CreateController();

        await controller.CreateContainerAsync(ValidConfig(hostPort: 9000));

        Assert.Contains("9000", _state.LastCreateBody!);
        Assert.Contains("PortBindings", _state.LastCreateBody!);
    }

    [Fact]
    public async Task CreateContainer_AutoHostPort_UsesZero()
    {
        _state.CreateResponseJson = $"{{\"Id\":\"{CreatedId}\",\"Warnings\":[]}}";
        var controller = CreateController();

        await controller.CreateContainerAsync(ValidConfig(hostPort: null));

        Assert.Contains("\"HostPort\":\"0\"", _state.LastCreateBody!);
    }

    [Fact]
    public async Task CreateContainer_DuplicateName_ThrowsBeforeCreate()
    {
        _state.ContainersListJson = ListJson(Summary("existing000001", "newone", state: "running"));
        var controller = CreateController();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.CreateContainerAsync(ValidConfig(name: "newone")));

        Assert.False(_state.PathRequested("/containers/create"));
    }

    [Theory]
    [InlineData("always")]
    [InlineData("unless-stopped")]
    [InlineData("on-failure")]
    [InlineData("no")]
    [InlineData("weird-default")]
    public async Task CreateContainer_RestartPolicyVariants_ReachCreate(string policy)
    {
        _state.CreateResponseJson = $"{{\"Id\":\"{CreatedId}\",\"Warnings\":[]}}";
        var controller = CreateController();

        await controller.CreateContainerAsync(ValidConfig(restartPolicy: policy));

        Assert.Contains("RestartPolicy", _state.LastCreateBody!);
    }

    [Fact]
    public async Task CreateContainer_DevicesVolumesEnvAndServerArgs_Encoded()
    {
        _state.CreateResponseJson = $"{{\"Id\":\"{CreatedId}\",\"Warnings\":[]}}";
        var controller = CreateController();
        var config = ValidConfig(
            devices: ["/dev/kfd"],
            volumes: [new VolumeMount { Host = "/tmp/data", Container = "/data", Readonly = true }],
            env: [new EnvVar { Key = "MY_VAR", Value = "1" }],
            serverArgs: ["--serve", "--port", "8080"]);

        await controller.CreateContainerAsync(config);

        var body = _state.LastCreateBody!;
        Assert.Contains("/dev/kfd", body);
        Assert.Contains("/tmp/data:/data:ro", body);
        Assert.Contains("MY_VAR=1", body);
        Assert.Contains("--serve", body);
    }

    [Fact]
    public async Task CreateContainer_InspectFailureAfterCreate_MappedPortNull()
    {
        _state.CreateResponseJson = $"{{\"Id\":\"{CreatedId}\",\"Warnings\":[]}}";
        // No inspect JSON for CreatedId → 404 → caught, mappedPort stays null.
        var controller = CreateController();

        var result = await controller.CreateContainerAsync(ValidConfig());

        Assert.Equal(CreatedId, result.ContainerId);
        Assert.Null(result.MappedPort);
    }

    [Fact]
    public async Task CreateContainer_PolicyViolation_ThrowsBeforeAnyHttpCall()
    {
        var controller = CreateController(); // default policy denies host networking
        var config = ValidConfig(networkMode: "host");

        await Assert.ThrowsAsync<ContainerPolicyException>(() => controller.CreateContainerAsync(config));

        // Denylist runs before the first HTTP request.
        Assert.Empty(_state.Requests);
    }
}
