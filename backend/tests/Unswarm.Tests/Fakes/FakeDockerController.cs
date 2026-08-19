using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeDockerController : IDockerController
{
    private int _nextPort = 8080;
    private int _nextId;

    /// <summary>Prefix for generated container ids (lets tests distinguish controllers).</summary>
    public string IdPrefix { get; set; } = "fake";

    public Func<string, CancellationToken, Task<ContainerStartResult>>? OnStart { get; set; }
    public Func<string, CancellationToken, Task>? OnStop { get; set; }
    public Func<string, CancellationToken, Task<ContainerStartResult>>? OnRestart { get; set; }

    public List<string> StartedModels { get; } = [];
    public List<string> StartedContainerIds { get; } = [];
    public List<string> StoppedContainerIds { get; } = [];
    public List<string> RestartedContainerIds { get; } = [];

    public bool FailStart { get; set; }
    public string? StartErrorMessage { get; set; }

    /// <summary>
    /// When set, StartRegisteredContainerAsync returns this MappedPort instead of a
    /// self-incremented one — lets tests point discovery at a real local listener.
    /// </summary>
    public int? MappedPortOverride { get; set; }

    /// <summary>Containers returned by ListContainersAsync (empty by default).</summary>
    public List<ContainerInfo> ListedContainers { get; set; } = [];

    private string NextId() => $"{IdPrefix}-{Interlocked.Increment(ref _nextId)}";

    public Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default)
    {
        StartedModels.Add(modelName);
        if (OnStart != null) return OnStart(modelName, ct);

        if (FailStart)
        {
            var failedId = NextId();
            StartedContainerIds.Add(failedId);
            return Task.FromResult(new ContainerStartResult
            {
                ContainerId = failedId,
                ErrorMessage = StartErrorMessage ?? $"Failed to start {modelName}"
            });
        }

        var id = NextId();
        StartedContainerIds.Add(id);
        return Task.FromResult(new ContainerStartResult
        {
            ContainerId = id,
            MappedPort = Interlocked.Increment(ref _nextPort)
        });
    }

    public Task<ContainerStartResult> StartRegisteredContainerAsync(
        string registeredContainerId,
        string image,
        int containerPort,
        string? gpuDevices,
        long memoryLimitMb,
        Dictionary<string, string> extraLabels,
        CancellationToken ct = default)
    {
        if (FailStart)
        {
            var failedId = NextId();
            StartedContainerIds.Add(failedId);
            return Task.FromResult(new ContainerStartResult
            {
                ContainerId = failedId,
                ErrorMessage = StartErrorMessage ?? $"Failed to start registered container {registeredContainerId}"
            });
        }

        var id = NextId();
        StartedContainerIds.Add(id);
        return Task.FromResult(new ContainerStartResult
        {
            ContainerId = id,
            MappedPort = MappedPortOverride ?? Interlocked.Increment(ref _nextPort)
        });
    }

    public Task StopContainerAsync(string idOrModel, CancellationToken ct = default)
    {
        StoppedContainerIds.Add(idOrModel);
        return OnStop != null ? OnStop(idOrModel, ct) : Task.CompletedTask;
    }

    public Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
    {
        RestartedContainerIds.Add(id);
        if (OnRestart != null) return OnRestart(id, ct);

        return Task.FromResult(new ContainerStartResult
        {
            ContainerId = id,
            MappedPort = Interlocked.Increment(ref _nextPort)
        });
    }

    public Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        return Task.FromResult<ContainerInspectResult?>(new ContainerInspectResult
        {
            Status = "running",
            Pid = 1234
        });
    }

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ContainerInfo>>(ListedContainers.ToList());
    }

    public Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task RemoveContainerAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}
