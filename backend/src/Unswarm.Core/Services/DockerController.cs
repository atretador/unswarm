using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using ContainerStatus = Unswarm.Core.Models.ContainerStatus;

namespace Unswarm.Core.Services;

public sealed class DockerController : IDockerController
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerController> _logger;

    private const string ManagedLabel = "unswarm.managed";
    private const string ModelLabel = "unswarm.model";
    private const string RegistryLabel = "unswarm.registry";

    public DockerController(ILogger<DockerController> logger)
    {
        _logger = logger;
        _client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
    }

    public async Task<ContainerStartResult> StartContainerAsync(string containerName, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting container {ContainerName}", containerName);

            // Find existing container by name (pre-provisioned)
            var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true
            }, ct).ConfigureAwait(false);

            var match = containers.FirstOrDefault(c => c.Names.Any(n =>
                    n.TrimStart('/').Equals(containerName, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidOperationException(
                    $"No existing container found with name '{containerName}'. " +
                    $"Create the container first (e.g. docker run --name {containerName} ...), then register it with Unswarm.");

            var containerId = match.ID;

            // Start if stopped
            if (match.State != "running")
            {
                _logger.LogInformation("Starting stopped container {ContainerId}", containerId[..12]);
                await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct)
                    .ConfigureAwait(false);
            }

            // Inspect to get mapped port
            var inspect = await _client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);

            int? mappedPort = null;
            if (inspect.NetworkSettings.Ports.TryGetValue("8080/tcp", out var bindings) && bindings is { Count: > 0 })
            {
                if (int.TryParse(bindings[0].HostPort, out var hp))
                    mappedPort = hp;
            }

            _logger.LogInformation("Container {ContainerId} started for {Name} on port {Port}",
                containerId[..12], containerName, mappedPort);

            return new ContainerStartResult
            {
                ContainerId = containerId,
                MappedPort = mappedPort
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start container {Name}", containerName);
            return new ContainerStartResult
            {
                ContainerId = string.Empty,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<ContainerStartResult> StartRegisteredContainerAsync(
        string registeredContainerId,
        string image,
        int containerPort,
        string? gpuDevices,
        long memoryLimitMb,
        Dictionary<string, string> extraLabels,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting registered container {RegisteredId} with image {Image}", registeredContainerId, image);

            // Find existing container by name (pre-provisioned; "image" field = container name)
            var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true // Include stopped containers
            }, ct).ConfigureAwait(false);

            var match = containers.FirstOrDefault(c => c.Names.Any(n =>
                    n.TrimStart('/').Equals(image, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidOperationException(
                    $"No existing container found with name '{image}'. " +
                    $"Create the container first (e.g. docker run --name {image} ...), then register it with Unswarm.");

            var containerId = match.ID;

            // Start if stopped
            if (match.State != "running")
            {
                _logger.LogInformation("Starting stopped container {ContainerId} (image {Image})",
                    containerId[..12], image);

                await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Container {ContainerId} already running (image {Image})",
                    containerId[..12], image);
            }

            // Inspect to get mapped port
            var inspect = await _client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);

            var portBinding = $"{containerPort}/tcp";
            int? mappedPort = null;
            if (inspect.NetworkSettings.Ports.TryGetValue(portBinding, out var bindings) && bindings is { Count: > 0 })
            {
                if (int.TryParse(bindings[0].HostPort, out var hp))
                    mappedPort = hp;
            }

            _logger.LogInformation("Registered container {RegisteredId} connected to {ContainerId} on port {Port}",
                registeredContainerId, containerId[..12], mappedPort);

            return new ContainerStartResult
            {
                ContainerId = containerId,
                MappedPort = mappedPort
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start registered container {RegisteredId}", registeredContainerId);
            return new ContainerStartResult
            {
                ContainerId = string.Empty,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task StopContainerAsync(string idOrModel, CancellationToken ct = default)
    {
        try
        {
            var containerId = await ResolveContainerIdAsync(idOrModel, ct).ConfigureAwait(false);
            if (containerId is null)
            {
                _logger.LogWarning("No container found for {IdOrModel}", idOrModel);
                return;
            }

            _logger.LogInformation("Stopping container {ContainerId}", containerId[..12]);

            await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters
            {
                WaitBeforeKillSeconds = 10
            }, ct).ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Container {IdOrModel} not found (already removed)", idOrModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop container {IdOrModel}", idOrModel);
        }
    }

    public async Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Restarting container {ContainerId}", id[..12]);

            await _client.Containers.StopContainerAsync(id, new ContainerStopParameters
            {
                WaitBeforeKillSeconds = 10
            }, ct).ConfigureAwait(false);

            await _client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct)
                .ConfigureAwait(false);

            var inspect = await _client.Containers.InspectContainerAsync(id, ct).ConfigureAwait(false);

            int? mappedPort = null;
            if (inspect.NetworkSettings.Ports.TryGetValue("8080/tcp", out var bindings) && bindings is { Count: > 0 })
            {
                if (int.TryParse(bindings[0].HostPort, out var hp))
                    mappedPort = hp;
            }

            return new ContainerStartResult
            {
                ContainerId = id,
                MappedPort = mappedPort
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart container {Id}", id);
            return new ContainerStartResult
            {
                ContainerId = id,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(id, ct).ConfigureAwait(false);

            return new ContainerInspectResult
            {
                Status = inspect.State.Status,
                Pid = (int?)inspect.State.Pid,
                MemoryMb = inspect.HostConfig.Memory / (1024 * 1024),
                CpuPercent = 0, // Docker.DotNet doesn't expose CPU percent directly
                UptimeSeconds = inspect.State.Running
                    ? (long)(DateTimeOffset.UtcNow - DateTimeOffset.Parse(inspect.State.StartedAt)).TotalSeconds
                    : 0
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        }, ct).ConfigureAwait(false);

        var result = new List<ContainerInfo>(containers.Count);
        foreach (var c in containers)
        {
            int? port = null;
            if (c.Ports != null)
            {
                // Find the first exposed port with a host mapping
                foreach (var p in c.Ports)
                {
                    if (p.PublicPort > 0)
                    {
                        port = (int)p.PublicPort;
                        break;
                    }
                }
            }

            var containerName = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12];

            string? registeredContainerId = null;
            if (c.Labels.TryGetValue(RegistryLabel, out var rcId)) registeredContainerId = rcId;

            result.Add(new ContainerInfo
            {
                Id = c.ID,
                ModelId = containerName,
                ModelName = containerName,
                Status = MapContainerStatus(c.State),
                Port = port,
                Pid = null,
                MemoryMb = c.SizeRw / (1024 * 1024),
                CpuPercent = 0,
                Uptime = (long)(DateTimeOffset.UtcNow - new DateTimeOffset(c.Created)).TotalSeconds,
                CreatedAt = new DateTimeOffset(c.Created),
                RegisteredRuntimeId = registeredContainerId
            });
        }

        return result;
    }

#pragma warning disable CS0618 // Obsolete API — MultiplexedStream not available in this version
    public async Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default)
    {
        var stream = await _client.Containers.GetContainerLogsAsync(id, new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Tail = tailLines.ToString()
        }, ct).ConfigureAwait(false);

        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            lines.Add(line);
        }
        return lines;
    }
#pragma warning restore CS0618

    public async Task RemoveContainerAsync(string id, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Removing container {ContainerId}", id[..12]);
            await _client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters
            {
                Force = true
            }, ct).ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogDebug("Container {Id} not found during removal (already removed)", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove container {Id}", id);
        }
    }

    private async Task<string?> ResolveContainerIdAsync(string idOrModel, CancellationToken ct)
    {
        // Try as direct container ID first
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(idOrModel, ct).ConfigureAwait(false);
            return inspect.ID;
        }
        catch
        {
            // Not a direct ID, search by name
        }

        // Search by container name
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        }, ct).ConfigureAwait(false);

        var match = containers.FirstOrDefault(c => c.Names.Any(n =>
            n.TrimStart('/').Equals(idOrModel, StringComparison.OrdinalIgnoreCase)));

        return match?.ID;
    }

    private static bool ImageMatches(string containerImage, string searchImage)
    {
        // Exact match
        if (containerImage.Equals(searchImage, StringComparison.OrdinalIgnoreCase))
            return true;

        // Match without tag (e.g. "image:latest" matches "image")
        var colonIndex = containerImage.LastIndexOf(':');
        if (colonIndex > 0)
        {
            var withoutTag = containerImage[..colonIndex];
            if (withoutTag.Equals(searchImage, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Partial match (container image contains search image)
        if (containerImage.Contains(searchImage, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public async Task<int?> ResolveMappedPortAsync(string containerName, int containerPort, CancellationToken ct = default)
    {
        try
        {
            var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true
            }, ct).ConfigureAwait(false);

            var match = containers.FirstOrDefault(c => c.Names.Any(n =>
                n.TrimStart('/').Equals(containerName, StringComparison.OrdinalIgnoreCase)));

            if (match is null)
            {
                _logger.LogDebug("Container {ContainerName} not found for port resolution", containerName);
                return null;
            }

            var inspect = await _client.Containers.InspectContainerAsync(match.ID, ct).ConfigureAwait(false);

            var portBinding = $"{containerPort}/tcp";
            if (inspect.NetworkSettings.Ports.TryGetValue(portBinding, out var bindings) && bindings is { Count: > 0 })
            {
                if (int.TryParse(bindings[0].HostPort, out var hp))
                {
                    _logger.LogDebug("Resolved mapped port for {ContainerName}: {Port} (containerPort {ContainerPort})",
                        containerName, hp, containerPort);
                    return hp;
                }
            }

            _logger.LogDebug("No port mapping found for {ContainerName} on containerPort {ContainerPort}", containerName, containerPort);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve mapped port for container {ContainerName}", containerName);
            return null;
        }
    }

    private static ContainerStatus MapContainerStatus(string state) => state.ToLowerInvariant() switch
    {
        "running" => ContainerStatus.Running,
        "created" or "restarting" => ContainerStatus.Starting,
        "stopping" => ContainerStatus.Stopping,
        "dead" => ContainerStatus.Error,
        "exited" => ContainerStatus.Stopped,
        _ => ContainerStatus.Error
    };
}
