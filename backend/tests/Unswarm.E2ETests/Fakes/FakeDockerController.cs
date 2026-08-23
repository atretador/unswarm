using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests.Fakes;

/// <summary>
/// In-memory Docker controller: records starts/stops and returns healthy
/// containers. Adapted from Unswarm.Tests/Fakes for the E2E host.
/// </summary>
public sealed class FakeDockerController : IDockerController
{
    private int _nextPort = 8080;
    private int _nextId;

    public string IdPrefix { get; set; } = "fake";

    public Func<string, string, CancellationToken, Task<ContainerStartResult>>? OnStartRegistered { get; set; }
    public Func<string, CancellationToken, Task>? OnStop { get; set; }

    public List<string> StartedModels { get; } = [];
    public List<string> StartedContainerIds { get; } = [];
    public List<string> StoppedContainerIds { get; } = [];

    /// <summary>Global ordering of lifecycle events across all controllers.</summary>
    public List<string> EventLog { get; } = [];

    private string NextId() => $"{IdPrefix}-{Interlocked.Increment(ref _nextId)}";

    public Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default)
        => StartRegisteredContainerAsync(modelName, modelName, 8080, null, 0, new Dictionary<string, string>(), ct);

    public Task<ContainerStartResult> StartRegisteredContainerAsync(
        string registeredContainerId,
        string image,
        int containerPort,
        string? gpuDevices,
        long memoryLimitMb,
        Dictionary<string, string> extraLabels,
        CancellationToken ct = default)
    {
        StartedModels.Add(image);
        if (OnStartRegistered is not null)
            return OnStartRegistered(registeredContainerId, image, ct);

        var id = NextId();
        StartedContainerIds.Add(id);
        lock (EventLog) EventLog.Add($"start:{registeredContainerId}:{id}");
        return Task.FromResult(new ContainerStartResult
        {
            ContainerId = id,
            MappedPort = Interlocked.Increment(ref _nextPort)
        });
    }

    public async Task StopContainerAsync(string idOrModel, CancellationToken ct = default)
    {
        StoppedContainerIds.Add(idOrModel);
        lock (EventLog) EventLog.Add($"stop:{idOrModel}");
        if (OnStop is not null) await OnStop(idOrModel, ct).ConfigureAwait(false);
    }

    public Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
    {
        var result = new ContainerStartResult
        {
            ContainerId = id,
            MappedPort = Interlocked.Increment(ref _nextPort)
        };
        lock (EventLog) EventLog.Add($"restart:{id}");
        return Task.FromResult(result);
    }

    public Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        var status = StoppedContainerIds.Contains(id) ? "exited" : "running";
        return Task.FromResult<ContainerInspectResult?>(new ContainerInspectResult { Status = status, Pid = 1234 });
    }

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContainerInfo>>([]);

    public Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task RemoveContainerAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}
