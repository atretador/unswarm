using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class AgentsControllerTests
{
    private readonly AgentRegistry _registry = new();
    private readonly FakeDockerController _docker = new();
    private readonly FakeContainerRegistry _containerRegistry = new();

    private FakeDockerControllerRouter CreateRouter()
        => new(new Dictionary<string, IDockerController> { ["host"] = _docker });

    private AgentsController CreateController(FakeDockerControllerRouter? router = null)
        => new(_registry, router ?? CreateRouter(), _containerRegistry);

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

    private static RegisteredContainer MakeRegistered(
        string id,
        string image,
        string agent = "host",
        string? runtimeContainerId = null,
        ContainerRegistrationStatus status = ContainerRegistrationStatus.Ready) => new()
    {
        Id = id,
        DisplayName = image,
        Image = image,
        Agent = agent,
        RuntimeContainerId = runtimeContainerId,
        Status = status,
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
        Assert.Equal(Environment.OSVersion.Platform.ToString(), host.OsPlatform);
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
                RegisteredContainerId = "reg-1"
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
}
