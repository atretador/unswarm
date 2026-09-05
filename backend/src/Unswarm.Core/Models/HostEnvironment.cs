namespace Unswarm.Core.Models;

/// <summary>
/// Detects whether the backend is running inside a Docker container.
/// Set via the RUNNING_IN_DOCKER environment variable in docker-compose.yml.
/// Used to gate features that require bare-metal host access (e.g. script execution).
/// </summary>
public static class HostEnvironment
{
    public static bool IsRunningInDocker { get; } =
        string.Equals(
            Environment.GetEnvironmentVariable("RUNNING_IN_DOCKER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
