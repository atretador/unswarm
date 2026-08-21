using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Remote;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class AgentsControllerTests
{
    private readonly AgentRegistry _registry = new();
    private readonly FakeDockerController _docker = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeHealthChecker _healthChecker = new();

    private FakeDockerControllerRouter CreateRouter()
        => new(new Dictionary<string, IDockerController> { ["host"] = _docker });

    private HostScriptRuntimeController CreateScriptController()
        => new(new LoggerFactory().CreateLogger<HostScriptRuntimeController>(),
            Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}"));

    private AgentsController CreateController(
        FakeDockerControllerRouter? router = null,
        HostScriptRuntimeController? scriptController = null)
        => new(_registry, router ?? CreateRouter(), _containerRegistry,
            scriptController ?? CreateScriptController(), _healthChecker);

    private static string CreateTestScript(HostScriptRuntimeController controller, string content)
    {
        // We need a script file that exists on disk. Create it in the temp dir.
        var path = Path.Combine(Path.GetTempPath(), $"test-script-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/bash\n{content}");
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        return path;
    }

    private static AgentConnection MakeConnection(string name) => new()
    {
        Name = name,
        ConnectionId = Guid.NewGuid().ToString("N"),
        ConnectedAt = DateTimeOffset.UtcNow,
        LastSeen = DateTimeOffset.UtcNow,
        IsConnected = true,
        Version = "test",
        Hostname = "agent-box",
        OsPlatform = "Linux",
        GpuInfo = "NVIDIA RTX 3090 (8GB)",
        TotalMemoryMb = 16384,
        CpuCores = 8
    };

    private static RegisteredRuntime MakeRegistered(
        string id,
        string image,
        string agent = "host",
        string? runtimeContainerId = null,
        ContainerRegistrationStatus status = ContainerRegistrationStatus.Ready,
        RuntimeKind runtimeKind = RuntimeKind.Container,
        string? launcherPath = null) => new()
    {
        Id = id,
        DisplayName = image,
        Image = image,
        Agent = agent,
        RuntimeContainerId = runtimeContainerId,
        Status = status,
        RuntimeKind = runtimeKind,
        LauncherPath = launcherPath,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task List_HostIsFirst()
    {
        _registry.Register("gpu1", MakeConnection("gpu1"), new FakeWebSocket());

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        Assert.NotEmpty(agents);
        Assert.Equal(ExecutionTarget.HostId, agents[0].Name);
        Assert.True(agents[0].IsConnected);
    }

    [Fact]
    public async Task List_IncludesConnectedAgents()
    {
        _registry.Register("gpu1", MakeConnection("gpu1"), new FakeWebSocket());
        _registry.Register("gpu2", MakeConnection("gpu2"), new FakeWebSocket());

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        Assert.Equal(3, agents.Count);
        Assert.Contains(agents, a => a.Name == "gpu1" && a.IsConnected && a.Hostname == "agent-box");
        Assert.Contains(agents, a => a.Name == "gpu2" && a.TotalMemoryMb == 16384);
    }

    [Fact]
    public async Task List_HostContainersIncluded_WhenRegistered()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "c1",
                ModelId = "llama",
                ModelName = "llama-3",
                Status = ContainerStatus.Running,
                Port = 8080
            }
        ];
        // The container appears only if it belongs to a registered container
        await _containerRegistry.CreateAsync(MakeRegistered("reg-1", "llama-3", runtimeContainerId: "c1"));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var host = agents[0];
        var container = Assert.Single(host.Containers);
        Assert.Equal("c1", container.ContainerId);
        Assert.Equal("llama-3", container.ModelName);
        Assert.Equal("running", container.Status);
        Assert.Equal(8080, container.Port);
    }

    [Fact]
    public async Task List_HostContainersFiltered_ToRegisteredOnly()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "c1",
                ModelId = "llama",
                ModelName = "llama-3",
                Status = ContainerStatus.Running,
                Port = 8080
            },
            new ContainerInfo
            {
                Id = "c2",
                ModelId = "unmanaged",
                ModelName = "unmanaged",
                Status = ContainerStatus.Running,
                Port = 9999
            }
        ];
        // Only c1 is a registered container
        await _containerRegistry.CreateAsync(MakeRegistered("reg-1", "llama-3", runtimeContainerId: "c1"));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var container = Assert.Single(agents[0].Containers);
        Assert.Equal("c1", container.ContainerId);
    }

    [Fact]
    public async Task List_AgentContainersFiltered_ToRegisteredOnly()
    {
        var connection = MakeConnection("gpu1");
        connection.Containers =
        [
            new AgentContainerStatus { ContainerId = "a1", ModelName = "vllm-serve", Status = "running", Port = 9000 },
            new AgentContainerStatus { ContainerId = "a2", ModelName = "unmanaged", Status = "running", Port = 9001 }
        ];
        _registry.Register("gpu1", connection, new FakeWebSocket());
        await _containerRegistry.CreateAsync(MakeRegistered("reg-remote", "vllm-serve", agent: "gpu1", runtimeContainerId: "a1"));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var agent = Assert.Single(agents, a => a.Name == "gpu1");
        var container = Assert.Single(agent.Containers);
        Assert.Equal("a1", container.ContainerId);
    }

    [Fact]
    public async Task List_HostPopulatesSystemTelemetry()
    {
        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var host = agents[0];
        Assert.Equal(Environment.MachineName, host.Hostname);
        Assert.Equal(RuntimeInformation.OSDescription, host.OsPlatform);
        Assert.Equal(Environment.ProcessorCount, host.CpuCores);
        Assert.True(host.IsConnected);
    }

    [Fact]
    public async Task ListAgentContainers_Host_ReturnsContainers()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "c1",
                ModelId = "llama",
                ModelName = "llama-3",
                Status = ContainerStatus.Running,
                Port = 8080
            }
        ];

        var result = await CreateController().ListAgentContainers("host", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var containers = Assert.IsAssignableFrom<List<ContainerResponse>>(ok.Value);
        var container = Assert.Single(containers);
        Assert.Equal("c1", container.Id);
        Assert.Equal("llama-3", container.ModelName);
    }

    [Fact]
    public async Task ListAgentContainers_UnknownAgent_ReturnsNotFound()
    {
        var result = await CreateController().ListAgentContainers("ghost", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task List_HostContainers_ImageMatch_CaseInsensitive()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "c1",
                ModelId = "LLAMA-3",
                ModelName = "LLAMA-3",
                Status = ContainerStatus.Running,
                Port = 8080
            }
        ];
        // Registered image is lowercase; the container name is reported in uppercase.
        await _containerRegistry.CreateAsync(MakeRegistered("reg-1", "llama-3"));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var container = Assert.Single(agents[0].Containers);
        Assert.Equal("c1", container.ContainerId);
    }

    [Fact]
    public async Task List_AgentNameComparison_CaseInsensitive()
    {
        var connection = MakeConnection("GPU1");
        connection.Containers =
        [
            new AgentContainerStatus { ContainerId = "a1", ModelName = "vllm-serve", Status = "running", Port = 9000 }
        ];
        _registry.Register("GPU1", connection, new FakeWebSocket());
        // Registration is stored with lowercase agent name.
        await _containerRegistry.CreateAsync(MakeRegistered("reg-remote", "vllm-serve", agent: "gpu1", runtimeContainerId: "a1"));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var agent = Assert.Single(agents, a => a.Name == "GPU1");
        var container = Assert.Single(agent.Containers);
        Assert.Equal("a1", container.ContainerId);
    }

    [Fact]
    public async Task List_HostContainers_RegisteredIdLabel_Authoritative_EvenWhenNameDiffers()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "c1",
                ModelId = "other-name",
                ModelName = "other-name",
                Status = ContainerStatus.Running,
                Port = 8080,
                RegisteredRuntimeId = "reg-1"
            }
        ];
        // The container carries the registry label; its reported name differs from the
        // registered image, but the label is authoritative evidence it is managed.
        await _containerRegistry.CreateAsync(MakeRegistered("reg-1", "llama-3", runtimeContainerId: "c1"));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var container = Assert.Single(agents[0].Containers);
        Assert.Equal("c1", container.ContainerId);
    }

    [Fact]
    public async Task List_ContainerWithErrorRegistration_Excluded_WhenNoRuntimeIdEvidence()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "c1",
                ModelId = "broken-serve",
                ModelName = "broken-serve",
                Status = ContainerStatus.Running,
                Port = 8080
            }
        ];
        // Error registration with no runtime id — its container must not leak.
        await _containerRegistry.CreateAsync(MakeRegistered("reg-err", "broken-serve", status: ContainerRegistrationStatus.Error));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        Assert.Empty(agents[0].Containers);
    }

    [Fact]
    public async Task List_ContainerWithErrorRegistration_ButRuntimeIdEvidence_StillIncluded()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "c1",
                ModelId = "broken-serve",
                ModelName = "broken-serve",
                Status = ContainerStatus.Running,
                Port = 8080
            }
        ];
        // Even an Error registration is authoritative when the container id matches
        // the recorded runtime container id.
        await _containerRegistry.CreateAsync(MakeRegistered("reg-err", "broken-serve", runtimeContainerId: "c1", status: ContainerRegistrationStatus.Error));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var container = Assert.Single(agents[0].Containers);
        Assert.Equal("c1", container.ContainerId);
    }

    [Fact]
    public async Task List_AgentScriptsPassedThrough()
    {
        var connection = MakeConnection("gpu1");
        connection.Scripts =
        [
            new AgentScriptStatus { Path = "/opt/scripts/model-a.sh", PID = 1234, Status = "running", Port = 9000, StartTime = 1700000000000 }
        ];
        _registry.Register("gpu1", connection, new FakeWebSocket());

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var agent = Assert.Single(agents, a => a.Name == "gpu1");
        var script = Assert.Single(agent.Scripts);
        Assert.Equal("/opt/scripts/model-a.sh", script.Path);
        Assert.Equal(1234, script.PID);
        Assert.Equal("running", script.Status);
        Assert.Equal(9000, script.Port);
    }

    [Fact]
    public async Task List_HostHasEmptyScripts_WhenNoRegisteredScripts()
    {
        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var host = agents[0];
        Assert.Empty(host.Scripts);
    }

    [Fact]
    public async Task ListAgentScripts_HostReturnsEmpty()
    {
        var result = await CreateController().ListAgentScripts("host", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var scripts = Assert.IsAssignableFrom<IReadOnlyList<AgentScriptStatus>>(ok.Value);
        Assert.Empty(scripts);
    }

    [Fact]
    public async Task ListAgentScripts_RemoteAgentReturnsScripts()
    {
        var connection = MakeConnection("gpu1");
        connection.Scripts =
        [
            new AgentScriptStatus { Path = "/opt/scripts/model-a.sh", PID = 1234, Status = "running", Port = 9000, StartTime = 1700000000000 },
            new AgentScriptStatus { Path = "/opt/scripts/model-b.sh", PID = 5678, Status = "stopped", Port = 9001, StartTime = 1700000000000 }
        ];
        _registry.Register("gpu1", connection, new FakeWebSocket());

        var result = await CreateController().ListAgentScripts("gpu1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var scripts = Assert.IsAssignableFrom<IReadOnlyList<AgentScriptStatus>>(ok.Value);
        Assert.Equal(2, scripts.Count);
        Assert.Equal("/opt/scripts/model-a.sh", scripts[0].Path);
        Assert.Equal("running", scripts[0].Status);
        Assert.Equal("/opt/scripts/model-b.sh", scripts[1].Path);
        Assert.Equal("stopped", scripts[1].Status);
    }

    [Fact]
    public async Task ListAgentScripts_UnknownAgentReturnsNotFound()
    {
        var result = await CreateController().ListAgentScripts("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── ListAvailableScripts endpoint tests ────────────────────────

    [Fact]
    public async Task ListAvailableScripts_Host_ReturnsBadRequest()
    {
        var result = await CreateController().ListAvailableScripts("host", CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = bad.Value!;
        var errorProp = error.GetType().GetProperty("error");
        Assert.NotNull(errorProp);
        Assert.Contains("Host scripts", (string)errorProp!.GetValue(error)!);
    }

    [Fact]
    public async Task ListAvailableScripts_UnknownAgent_ReturnsNotFound()
    {
        var result = await CreateController().ListAvailableScripts("ghost", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ListAvailableScripts_Success_ReturnsScriptArray()
    {
        var fakeRemote = new FakeRemoteDockerController
        {
            ListedScripts =
            [
                new AgentScriptInfo { Path = "/opt/scripts/model-a.sh", Name = "model-a" },
                new AgentScriptInfo { Path = "/opt/scripts/model-b.sh", Name = "model-b" }
            ]
        };

        _registry.Register("gpu1", MakeConnection("gpu1"), new FakeWebSocket());

        var agentTarget = ExecutionTarget.ForAgent("gpu1").Id;
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController>
            {
                ["host"] = _docker,
                [agentTarget] = fakeRemote
            });

        var result = await CreateController(router).ListAvailableScripts("gpu1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var scripts = Assert.IsAssignableFrom<IReadOnlyList<AgentScriptInfo>>(ok.Value);
        Assert.Equal(2, scripts.Count);
        Assert.Equal("/opt/scripts/model-a.sh", scripts[0].Path);
        Assert.Equal("model-a", scripts[0].Name);
        Assert.Equal("/opt/scripts/model-b.sh", scripts[1].Path);
        Assert.Equal("model-b", scripts[1].Name);
    }

    [Fact]
    public async Task ListAvailableScripts_Unreachable_Returns503()
    {
        _registry.Register("gpu1", MakeConnection("gpu1"), new FakeWebSocket());

        // Agent is registered but NOT in the reachable set
        var agentTarget = ExecutionTarget.ForAgent("gpu1").Id;
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController>
            {
                ["host"] = _docker,
                [agentTarget] = new FakeRemoteDockerController()
            },
            reachable: ["host"]); // gpu1 is NOT reachable

        var result = await CreateController(router).ListAvailableScripts("gpu1", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task ListAvailableScripts_CommandFailure_Returns503()
    {
        var fakeRemote = new FakeRemoteDockerController
        {
            ThrowOnListScripts = new InvalidOperationException("Agent 'gpu1' is not connected")
        };

        _registry.Register("gpu1", MakeConnection("gpu1"), new FakeWebSocket());

        var agentTarget = ExecutionTarget.ForAgent("gpu1").Id;
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController>
            {
                ["host"] = _docker,
                [agentTarget] = fakeRemote
            });

        var result = await CreateController(router).ListAvailableScripts("gpu1", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    // ── Host scripts from registry tests ─────────────────────────────

    [Fact]
    public async Task List_HostScriptsSurfacedFromRegistry()
    {
        var sc = CreateScriptController();
        var script = CreateTestScript(sc, "while true; do sleep 1; done");
        // Start a real process so the script is tracked
        var start = await sc.StartScriptAsync("reg-script-1", script, 9000);
        Assert.NotNull(start.Pid);

        // Register a host script runtime
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-script-1",
            DisplayName = "model-script",
            Image = "model-script",
            Agent = "host",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            RuntimeProcessId = start.Pid,
            MappedPort = 9000,
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var result = await CreateController(scriptController: sc).List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var host = agents[0];
        var scriptStatus = Assert.Single(host.Scripts);
        Assert.Equal(script, scriptStatus.Path);

        await sc.StopScriptAsync("reg-script-1");
    }

    [Fact]
    public async Task ListAgentScripts_HostReturnsScriptsFromRegistry()
    {
        var sc = CreateScriptController();
        var script = CreateTestScript(sc, "while true; do sleep 1; done");
        var start = await sc.StartScriptAsync("reg-script-1", script, 9000);
        Assert.NotNull(start.Pid);

        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-script-1",
            DisplayName = "model-script",
            Image = "model-script",
            Agent = "host",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            RuntimeProcessId = start.Pid,
            MappedPort = 9000,
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var result = await CreateController(scriptController: sc).ListAgentScripts("host", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var scripts = Assert.IsAssignableFrom<IReadOnlyList<AgentScriptStatus>>(ok.Value);
        var scriptStatus = Assert.Single(scripts);
        Assert.Equal(script, scriptStatus.Path);

        await sc.StopScriptAsync("reg-script-1");
    }

    [Fact]
    public async Task List_HostScriptHealthGate_DowngradeWhenNotHealthy()
    {
        var sc = CreateScriptController();
        var script = CreateTestScript(sc, "while true; do sleep 1; done");
        // Start a real process tracked by the controller
        var start = await sc.StartScriptAsync("reg-script-starting", script, 9000);
        Assert.NotNull(start.Pid);

        // Register a host script runtime in Starting state (not healthy)
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-script-starting",
            DisplayName = "model-a",
            Image = "model-a",
            Agent = "host",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            RuntimeProcessId = start.Pid,
            MappedPort = 9000,
            Status = ContainerRegistrationStatus.Starting,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        _healthChecker.IsReady = false;

        var result = await CreateController(scriptController: sc).List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var host = agents[0];
        var scriptStatus = Assert.Single(host.Scripts);
        // Process is alive but health not ready → "starting"
        Assert.Equal("starting", scriptStatus.Status);

        await sc.StopScriptAsync("reg-script-starting");
    }

    [Fact]
    public async Task List_HostScriptHealthGate_RunningWhenHealthy()
    {
        var sc = CreateScriptController();
        var script = CreateTestScript(sc, "while true; do sleep 1; done");
        // Start a real process tracked by the controller
        var start = await sc.StartScriptAsync("reg-script-ready", script, 9000);
        Assert.NotNull(start.Pid);

        // Register a host script runtime in Ready state
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-script-ready",
            DisplayName = "model-a",
            Image = "model-a",
            Agent = "host",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            RuntimeProcessId = start.Pid,
            MappedPort = 9000,
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        _healthChecker.IsReady = true;

        var result = await CreateController(scriptController: sc).List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var host = agents[0];
        var scriptStatus = Assert.Single(host.Scripts);
        Assert.Equal("running", scriptStatus.Status);

        await sc.StopScriptAsync("reg-script-ready");
    }

    [Fact]
    public async Task List_HostScriptProcessDead_ShowsStopped()
    {
        var sc = CreateScriptController();
        // Register a host script runtime but don't actually start a process
        await _containerRegistry.CreateAsync(MakeRegistered("reg-script-dead", "model-a",
            runtimeKind: RuntimeKind.Script, launcherPath: "/opt/scripts/model-a.sh"));
        await _containerRegistry.UpdateAsync("reg-script-dead",
            (await _containerRegistry.GetAsync("reg-script-dead"))! with
            {
                RuntimeProcessId = 999,
                MappedPort = 9000,
                Status = ContainerRegistrationStatus.Error
            });

        var result = await CreateController(scriptController: sc).List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var host = agents[0];
        var script = Assert.Single(host.Scripts);
        Assert.Equal("stopped", script.Status);
    }

    [Fact]
    public async Task List_AgentScriptsPath_UnaffectedByHealthGate()
    {
        var connection = MakeConnection("gpu1");
        connection.Scripts =
        [
            new AgentScriptStatus { Path = "/opt/scripts/model-a.sh", PID = 1234, Status = "running", Port = 9000, StartTime = 1700000000000 }
        ];
        _registry.Register("gpu1", connection, new FakeWebSocket());

        // Register a matching runtime in Starting state
        await _containerRegistry.CreateAsync(MakeRegistered("reg-remote", "model-a",
            agent: "gpu1", runtimeKind: RuntimeKind.Script, launcherPath: "/opt/scripts/model-a.sh"));

        var result = await CreateController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<List<AgentInfo>>(ok.Value);

        var agent = Assert.Single(agents, a => a.Name == "gpu1");
        // Remote agent scripts are not health-gated (only host scripts are)
        var script = Assert.Single(agent.Scripts);
        Assert.Equal("running", script.Status);
    }
}
