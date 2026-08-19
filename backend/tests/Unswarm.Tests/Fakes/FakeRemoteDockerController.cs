using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>One step of a scripted health-probe sequence.</summary>
public sealed class HealthProbeStep
{
    /// <summary>Healthy flag to return (when <see cref="Throw"/> is null).</summary>
    public bool? Healthy { get; init; }

    /// <summary>When set, the probe throws this exception instead of returning.</summary>
    public Exception? Throw { get; init; }
}

/// <summary>
/// Test IRemoteDockerController for exercising agent-registration paths. The
/// controller records the images it was asked to start and the ports it was asked
/// to health-check, and returns scripted results for start/list/health/discovery.
/// </summary>
public sealed class FakeRemoteDockerController : IRemoteDockerController
{
    /// <summary>Result returned by StartRegisteredContainerAsync.</summary>
    public ContainerStartResult StartResult { get; set; } = new()
    {
        ContainerId = "remote-c1",
        MappedPort = 9090
    };

    /// <summary>Containers returned by ListContainersAsync.</summary>
    public List<ContainerInfo> ListedContainers { get; set; } = [];

    /// <summary>Value returned by HealthCheckAsync while no script/throw is active.</summary>
    public bool Healthy { get; set; } = true;

    /// <summary>If set, every HealthCheckAsync call throws this exception.</summary>
    public Exception? ThrowOnHealth { get; set; }

    /// <summary>
    /// Optional scripted probe sequence. Each call consumes one step: throw if the
    /// step has a Throw, else return step.Healthy. When the script is exhausted the
    /// regular Healthy/ThrowOnHealth behavior applies.
    /// </summary>
    public Queue<HealthProbeStep>? HealthProbeScript { get; set; }

    /// <summary>Models returned by DiscoverModelsAsync.</summary>
    public List<DiscoveredModel> Discovered { get; set; } = [];

    public List<string> StartedImages { get; } = [];
    public List<int> HealthCheckedPorts { get; } = [];

    public Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default)
        => Task.FromResult(StartResult);

    public Task<ContainerStartResult> StartRegisteredContainerAsync(
        string registeredContainerId,
        string image,
        int containerPort,
        string? gpuDevices,
        long memoryLimitMb,
        Dictionary<string, string> extraLabels,
        CancellationToken ct = default)
    {
        StartedImages.Add(image);
        return Task.FromResult(StartResult);
    }

    public Task StopContainerAsync(string idOrModel, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
        => Task.FromResult(StartResult);

    public Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
        => Task.FromResult<ContainerInspectResult?>(new ContainerInspectResult { Status = "running" });

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContainerInfo>>(ListedContainers.ToList());

    public Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task RemoveContainerAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> HealthCheckAsync(int port, CancellationToken ct = default)
    {
        HealthCheckedPorts.Add(port);
        if (HealthProbeScript is { Count: > 0 })
        {
            var step = HealthProbeScript.Dequeue();
            if (step.Throw is not null)
                throw step.Throw;
            return Task.FromResult(step.Healthy ?? Healthy);
        }

        if (ThrowOnHealth is not null)
            throw ThrowOnHealth;
        return Task.FromResult(Healthy);
    }

    public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiscoveredModel>>(Discovered.ToList());
}
