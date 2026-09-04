namespace Unswarm.Core.Models;

/// <summary>
/// Configures the host address used to reach model containers from the backend process.
/// Default (127.0.0.1) works for bare-metal / dotnet-run where the backend and model
/// containers share the host network. In Docker, set to "host.docker.internal" so the
/// backend container can reach model-container ports mapped to the Docker host.
/// </summary>
public sealed class ContainerHostOptions
{
    public const string SectionName = "ContainerHost";

    public string Host { get; set; } = "127.0.0.1";
}
