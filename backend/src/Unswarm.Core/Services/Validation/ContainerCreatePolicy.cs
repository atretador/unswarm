using System.Text.RegularExpressions;
using Unswarm.Core.Contracts;

namespace Unswarm.Core.Services.Validation;

/// <summary>
/// Configuration for the role-free global container-creation denylist.
/// Bound from the "Docker" configuration section.
/// </summary>
public sealed class DockerPolicyOptions
{
    public const string SectionName = "Docker";

    /// <summary>Allow Docker host networking unless explicitly opted in.</summary>
    public bool AllowHostNetwork { get; set; }

    /// <summary>Allow sharing the host IPC namespace unless explicitly opted in.</summary>
    public bool AllowHostIpc { get; set; }

    /// <summary>
    /// Device path prefixes allowed for GPU passthrough. Empty means no device
    /// is allowed.
    /// </summary>
    public string[] AllowedDevicePrefixes { get; set; } = ["/dev/kfd", "/dev/dri/", "/dev/nvidia"];
}

/// <summary>Thrown when a container-create config violates the global policy.</summary>
public sealed class ContainerPolicyException(string message) : Exception(message);

/// <summary>
/// Role-free global safety net for container creation. Applies to EVERY caller
/// (local controller invocation and the Docker controller itself); Admin role
/// checks live in the API layer, never here. This denies dangerous host-level
/// capabilities and protected/symlinked host paths.
/// </summary>
public static class ContainerCreatePolicy
{
    private static readonly Regex EnvKeyPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly string[] HardDeniedPaths = ["/etc", "/proc", "/sys", "/dev", "/root", "/boot"];

    public static void Validate(ContainerCreateConfig config, DockerPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        ValidateNetworkMode(config.NetworkMode, options);
        ValidateIpcMode(config.IpcMode, options);
        ValidatePort(config.ContainerPort, nameof(config.ContainerPort));
        if (config.HostPort.HasValue)
            ValidatePort(config.HostPort.Value, nameof(config.HostPort));

        // 0 is "unset" for parity with the agent; only non-zero values outside
        // the supported range are rejected.
        if (config.ShmSizeMb != 0 && (config.ShmSizeMb < 64 || config.ShmSizeMb > 65536))
            throw new ContainerPolicyException("ShmSizeMb must be 0 (unset) or between 64 and 65536.");

        if (config.ServerArgs is { Count: > 0 } args)
        {
            if (args.Count > 64)
                throw new ContainerPolicyException("ServerArgs may not exceed 64 entries.");
            foreach (var arg in args)
            {
                if (arg is null || arg.Length > 4096)
                    throw new ContainerPolicyException("Each ServerArgs entry must be at most 4096 characters.");
            }
        }

        if (config.Env is { Count: > 0 } env)
        {
            foreach (var item in env)
            {
                if (item.Key is null || !EnvKeyPattern.IsMatch(item.Key))
                    throw new ContainerPolicyException($"Environment variable name '{item.Key}' is invalid.");
            }
        }

        if (config.Volumes is { Count: > 0 } volumes)
        {
            foreach (var volume in volumes)
                ValidateVolume(volume.Host);
        }

        if (config.Devices is { Count: > 0 } devices)
        {
            foreach (var device in devices)
                ValidateDevice(device, options);
        }
    }

    private static void ValidateNetworkMode(string? networkMode, DockerPolicyOptions options)
    {
        var mode = (networkMode ?? string.Empty).Trim();
        if (mode.Length == 0) return;
        if (mode.Equals("bridge", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;

        // Everything else ("host", "container:*", ...) requires an explicit opt-in.
        if (!options.AllowHostNetwork)
            throw new ContainerPolicyException($"Network mode '{mode}' is not allowed.");
    }

    private static void ValidateIpcMode(string? ipcMode, DockerPolicyOptions options)
    {
        var mode = (ipcMode ?? string.Empty).Trim();
        if (mode.Length == 0 || mode.Equals("private", StringComparison.OrdinalIgnoreCase))
            return;

        if (!options.AllowHostIpc)
            throw new ContainerPolicyException($"IPC mode '{mode}' is not allowed.");
    }

    private static void ValidatePort(int port, string name)
    {
        // 0 is "unset" for parity with the agent; only out-of-range non-zero
        // values are rejected.
        if (port < 0 || port > 65535)
            throw new ContainerPolicyException($"{name} must be 0 (unset) or between 1 and 65535.");
    }

    private static void ValidateVolume(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ContainerPolicyException("Volume host path is required.");
        if (!Path.IsPathRooted(host))
            throw new ContainerPolicyException($"Volume host path must be absolute: {host}");

        string normalized;
        try
        {
            normalized = NormalizeResolved(host);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ContainerPolicyException($"Volume host path '{host}' is not a valid path.");
        }

        if (normalized == "/")
            throw new ContainerPolicyException($"Volume host path '{host}' targets a protected path.");

        foreach (var denied in HardDeniedPaths)
        {
            if (IsAtOrUnder(normalized, denied))
                throw new ContainerPolicyException($"Volume host path '{host}' targets a protected path ({denied}).");
        }

        // Container-runtime sockets anywhere in the path.
        foreach (var component in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component.Equals("docker.sock", StringComparison.OrdinalIgnoreCase)
                || component.Equals("containerd.sock", StringComparison.OrdinalIgnoreCase))
            {
                throw new ContainerPolicyException($"Volume host path '{host}' targets a container runtime socket.");
            }
        }
    }

    private static void ValidateDevice(string? device, DockerPolicyOptions options)
    {
        if (string.IsNullOrWhiteSpace(device))
            throw new ContainerPolicyException("Device path is required.");

        string normalized;
        try
        {
            normalized = NormalizeResolved(device);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ContainerPolicyException($"Device path '{device}' is not a valid path.");
        }

        if (!IsAtOrUnder(normalized, "/dev"))
            throw new ContainerPolicyException($"Device path '{device}' must be under /dev.");

        // Device prefixes are raw (not path-component bounded): "/dev/nvidia" is
        // meant to allow "/dev/nvidia0", "/dev/nvidiactl", etc.
        var prefixes = options.AllowedDevicePrefixes ?? [];
        var allowed = prefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Any(p => normalized.StartsWith(p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        if (!allowed)
            throw new ContainerPolicyException($"Device path '{device}' is not an allowed device.");
    }

    private static string NormalizeResolved(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > 1)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) full = "/";
        return ResolveSymlinks(full);
    }

    /// <summary>
    /// Resolves symlinked path components so a <c>/tmp/x → /</c> link cannot
    /// bypass the prefix checks. Non-existent components are left as-is.
    /// Internal so launcher-containment checks can reuse the same resolution.
    /// </summary>
    internal static string ResolveSymlinks(string absolutePath)
    {
        var root = Path.GetPathRoot(absolutePath);
        if (string.IsNullOrEmpty(root)) return absolutePath;

        var current = root;
        var remainder = absolutePath[root.Length..];
        foreach (var segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);

            FileSystemInfo? target = null;
            try { target = File.ResolveLinkTarget(next, returnFinalTarget: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (target is null)
            {
                try { target = Directory.ResolveLinkTarget(next, returnFinalTarget: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            if (target is not null)
                next = target.FullName;

            current = next;
        }

        return current;
    }

    private static bool IsAtOrUnder(string path, string prefix) =>
        path.Equals(prefix, StringComparison.Ordinal)
        || path.StartsWith(prefix + "/", StringComparison.Ordinal);
}
