using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Remote;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Scriptable remote docker controller for coverage tests that need to force
/// agent-side failures (container listing, script upload/update/read/delete).
/// </summary>
public sealed class FakeRemoteDockerControllerCoverage2 : IRemoteDockerController
{
    public List<ContainerInfo> ListedContainers { get; set; } = [];
    public Exception? ListContainersException { get; set; }

    public List<AgentScriptInfo> ListedScripts { get; set; } = [];
    public Exception? ListScriptsException { get; set; }

    public Exception? UploadException { get; set; }
    public Exception? UpdateException { get; set; }
    public Exception? GetContentException { get; set; }
    public Exception? DeleteException { get; set; }

    public string Content { get; set; } = "#!/bin/bash\necho hello";

    public List<(string Name, string Content)> Uploaded { get; } = [];
    public List<(string Name, string Content)> Updated { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<string> ContentRequests { get; } = [];

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
        => ListContainersException is not null
            ? Task.FromException<IReadOnlyList<ContainerInfo>>(ListContainersException)
            : Task.FromResult<IReadOnlyList<ContainerInfo>>(ListedContainers.ToList());

    public Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default)
        => Task.FromResult(new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 });

    public Task<ContainerStartResult> StartRegisteredContainerAsync(
        string registeredContainerId, string image, int containerPort, string? gpuDevices,
        long memoryLimitMb, Dictionary<string, string> extraLabels, CancellationToken ct = default)
        => Task.FromResult(new ContainerStartResult { ContainerId = "remote-c1", MappedPort = 9090 });

    public Task StopContainerAsync(string idOrModel, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
        => Task.FromResult(new ContainerStartResult { ContainerId = id, MappedPort = 9090 });

    public Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
        => Task.FromResult<ContainerInspectResult?>(new ContainerInspectResult { Status = "running" });

    public Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task RemoveContainerAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<int?> ResolveMappedPortAsync(string containerName, int containerPort, CancellationToken ct = default)
        => Task.FromResult<int?>(9090);

    public Task<bool> HealthCheckAsync(int port, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiscoveredModel>>([]);

    public Task<string> InferAsync(int port, string requestJson, CancellationToken ct = default)
        => Task.FromResult("{}");

    public Task<Stream> InferStreamAsync(int port, string requestJson, CancellationToken ct = default)
        => Task.FromResult<Stream>(new MemoryStream("{}"u8.ToArray()));

    public Task<IReadOnlyList<AgentScriptInfo>> ListScriptsAsync(CancellationToken ct = default)
        => ListScriptsException is not null
            ? Task.FromException<IReadOnlyList<AgentScriptInfo>>(ListScriptsException)
            : Task.FromResult<IReadOnlyList<AgentScriptInfo>>(ListedScripts.ToList());

    public Task<AgentScriptInfo> UploadScriptAsync(string name, string content, CancellationToken ct = default)
    {
        if (UploadException is not null) return Task.FromException<AgentScriptInfo>(UploadException);
        Uploaded.Add((name, content));
        return Task.FromResult(new AgentScriptInfo { Name = name, Path = name });
    }

    public Task<AgentScriptInfo> UpdateScriptAsync(string name, string content, CancellationToken ct = default)
    {
        if (UpdateException is not null) return Task.FromException<AgentScriptInfo>(UpdateException);
        Updated.Add((name, content));
        return Task.FromResult(new AgentScriptInfo { Name = name, Path = name });
    }

    public Task<string> GetScriptContentAsync(string path, CancellationToken ct = default)
    {
        if (GetContentException is not null) return Task.FromException<string>(GetContentException);
        ContentRequests.Add(path);
        return Task.FromResult(Content);
    }

    public Task DeleteScriptAsync(string path, CancellationToken ct = default)
    {
        if (DeleteException is not null) return Task.FromException(DeleteException);
        Deleted.Add(path);
        return Task.CompletedTask;
    }

    public Task<string> PullImageAsync(string image, CancellationToken ct = default) => Task.FromResult(image);

    public Task<ContainerCreateResult> CreateContainerAsync(ContainerCreateConfig config, CancellationToken ct = default)
        => Task.FromResult(new ContainerCreateResult { ContainerId = "remote-created-1" });
}
