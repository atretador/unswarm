using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

/// <summary>
/// Orchestrates creating a new Docker container from an image, registering it,
/// and optionally starting it. Handles transaction safety, idempotency, and
/// failure log capture.
/// </summary>
public interface IContainerCreationService
{
    /// <summary>
    /// Creates a container from a Docker image. The full flow is:
    /// 1. Validate (name uniqueness, coexistence policy)
    /// 2. Pull image (if remote agent)
    /// 3. Create container via Docker
    /// 4. Register in DB (within transaction)
    /// 5. Optionally start and discover models
    /// </summary>
    Task<CreateContainerResult> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default);

    /// <summary>
    /// Gets the current status of a creation request.
    /// </summary>
    Task<CreateContainerResult> GetCreateStatusAsync(string runtimeId, CancellationToken ct = default);
}

public sealed record CreateContainerRequest(
    string Image,
    string Name,
    DockerCreateParams DockerParams,
    string Agent = "host",
    bool Detach = false);

public sealed record DockerCreateParams(
    string? ContainerName,
    int ContainerPort = 8080,
    int? HostPort = null,
    IReadOnlyList<string>? Devices = null,
    IReadOnlyList<VolumeMount>? Volumes = null,
    IReadOnlyList<EnvVar>? Env = null,
    int ShmSizeMb = 16384,
    string IpcMode = "private",
    string NetworkMode = "bridge",
    string RestartPolicy = "unless-stopped",
    IReadOnlyList<string>? ServerArgs = null);

public sealed record CreateContainerResult(
    string RuntimeId,
    ContainerCreationStatus Status,
    string? ContainerId,
    string? Message,
    string? ErrorDetail);

public enum ContainerCreationStatus
{
    Pulling,
    Creating,
    Starting,
    Running,
    Failed
}
