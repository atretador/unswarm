using System.Text;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Remote;

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

    /// <summary>
    /// Scriptable inference behavior. When set, called with (port, requestJson, ct).
    /// Return the raw response body string. If it throws, the call is treated as failure.
    /// </summary>
    public Func<int, string, CancellationToken, Task<string>>? InferFunc { get; set; }

    /// <summary>Default raw body returned by InferAsync when no InferFunc is set.</summary>
    public string InferResult { get; set; } =
        """{"id":"chatcmpl-test","choices":[{"message":{"role":"assistant","content":"hi"}}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}""";

    public List<string> StartedImages { get; } = [];
    public List<int> HealthCheckedPorts { get; } = [];
    public List<(int Port, string RequestJson)> InferCalls { get; } = [];

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

    public Task<int?> ResolveMappedPortAsync(string containerName, int containerPort, CancellationToken ct = default)
        => Task.FromResult<int?>(StartResult.MappedPort);

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

    public Task<string> InferAsync(int port, string requestJson, CancellationToken ct = default)
    {
        lock (InferCalls) InferCalls.Add((port, requestJson));
        return InferFunc is not null
            ? InferFunc(port, requestJson, ct)
            : Task.FromResult(InferResult);
    }

    /// <summary>
    /// Scriptable streaming inference. When set, called with (port, requestJson, ct)
    /// and its returned stream is handed to the caller. When null, the buffered
    /// InferResult is wrapped in a MemoryStream (simulating an old agent fallback).
    /// </summary>
    public Func<int, string, CancellationToken, Task<Stream>>? InferStreamFunc { get; set; }

    public Task<Stream> InferStreamAsync(int port, string requestJson, CancellationToken ct = default)
    {
        lock (InferCalls) InferCalls.Add((port, requestJson));
        return InferStreamFunc is not null
            ? InferStreamFunc(port, requestJson, ct)
            : Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(InferResult)));
    }

    /// <summary>Scripts returned by ListScriptsAsync.</summary>
    public List<AgentScriptInfo> ListedScripts { get; set; } = [];

    /// <summary>If set, ListScriptsAsync throws this exception.</summary>
    public Exception? ThrowOnListScripts { get; set; }

    public Task<IReadOnlyList<AgentScriptInfo>> ListScriptsAsync(CancellationToken ct = default)
    {
        if (ThrowOnListScripts is not null)
            throw ThrowOnListScripts;
        return Task.FromResult<IReadOnlyList<AgentScriptInfo>>(ListedScripts.ToList());
    }

    public Task<AgentScriptInfo> UploadScriptAsync(string name, string content, CancellationToken ct = default)
        => Task.FromResult(new AgentScriptInfo { Name = name, Path = name });

    public Task<AgentScriptInfo> UpdateScriptAsync(string name, string content, CancellationToken ct = default)
        => Task.FromResult(new AgentScriptInfo { Name = name, Path = name });

    public Task<string> GetScriptContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult("");
}
