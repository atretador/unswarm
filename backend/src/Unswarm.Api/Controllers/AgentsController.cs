using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Remote;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Lists execution targets ("host" + connected remote agents) with enriched
/// telemetry so callers can see where models can run. Containers are filtered to
/// the registered set so unmanaged containers never surface.
/// </summary>
[ApiController]
// Execution-target listing. The admin cookie (dashboard) OR an agent API key
// may call it; an inference key is rejected by the scope policy.
[Authorize]
[Route("api/[controller]")]
public sealed class AgentsController : ControllerBase
{
    private readonly IAgentRegistry _registry;
    private readonly IDockerControllerRouter _router;
    private readonly IContainerRegistry _containerRegistry;
    private readonly HostScriptRuntimeController _scriptController;
    private readonly IHealthChecker _healthChecker;

    public AgentsController(
        IAgentRegistry registry,
        IDockerControllerRouter router,
        IContainerRegistry containerRegistry,
        HostScriptRuntimeController scriptController,
        IHealthChecker healthChecker)
    {
        _registry = registry;
        _router = router;
        _containerRegistry = containerRegistry;
        _scriptController = scriptController;
        _healthChecker = healthChecker;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var allRegistered = await _containerRegistry.ListAllAsync(ct).ConfigureAwait(false);

        var agents = new List<AgentInfo>
        {
            await BuildHostInfoAsync(allRegistered, ct).ConfigureAwait(false)
        };

        foreach (var info in _registry.ListWithInfo())
        {
            agents.Add(FilterAgentContainers(info, allRegistered));
        }

        return Ok(agents);
    }

    [HttpGet("{name}/scripts")]
    public async Task<IActionResult> ListAgentScripts(string name, CancellationToken ct = default)
    {
        if (string.Equals(name, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase))
        {
            var allRegistered = await _containerRegistry.ListAllAsync(ct).ConfigureAwait(false);
            var hostScripts = await BuildHostScriptsAsync(allRegistered, ct).ConfigureAwait(false);
            return Ok(hostScripts);
        }

        var info = _registry.GetInfo(name);
        if (info is null)
            return NotFound(new { error = $"Agent '{name}' not found" });

        return Ok(info.Scripts ?? []);
    }

    /// <summary>
    /// Lists launcher scripts available on a remote agent by querying the agent
    /// over WebSocket. Host is rejected (host scripts are registered by full path,
    /// not discovered remotely).
    /// </summary>
    [HttpGet("{name}/scripts/available")]
    public async Task<IActionResult> ListAvailableScripts(string name, CancellationToken ct)
    {
        if (string.Equals(name, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Host scripts are registered by full path; use /api/agents/host/scripts instead" });

        var info = _registry.GetInfo(name);
        if (info is null)
            return NotFound(new { error = $"Agent '{name}' not found" });

        var targetId = ExecutionTarget.ForAgent(name).Id;
        if (!_router.IsTargetReachable(targetId))
            return StatusCode(503, new { error = $"Agent '{name}' is not reachable" });

        try
        {
            var controller = _router.GetController(targetId);
            var remote = (IRemoteDockerController)controller;
            var scripts = await remote.ListScriptsAsync(ct).ConfigureAwait(false);
            return Ok(scripts);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return StatusCode(503, new { error = $"Failed to list scripts on agent '{name}': {ex.Message}" });
        }
    }

    [HttpGet("{name}/containers")]
    public async Task<IActionResult> ListAgentContainers(string name, CancellationToken ct)
    {
        var target = string.Equals(name, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase)
            ? ExecutionTarget.HostId
            : ExecutionTarget.ForAgent(name).Id;

        if (!_router.IsTargetReachable(target))
            return NotFound(new { error = $"Agent '{name}' is not reachable" });

        var list = await _router.GetController(target).ListContainersAsync(ct).ConfigureAwait(false);
        return Ok(list.Select(ContainerResponse.FromContainerInfo).ToList());
    }

    private async Task<AgentInfo> BuildHostInfoAsync(IReadOnlyList<RegisteredRuntime> allRegistered, CancellationToken ct)
    {
        var containers = await _router.GetController(ExecutionTarget.HostId).ListContainersAsync(ct).ConfigureAwait(false);
        var scripts = await BuildHostScriptsAsync(allRegistered, ct).ConfigureAwait(false);

        return new AgentInfo
        {
            Name = ExecutionTarget.HostId,
            IsConnected = true,
            Hostname = Environment.MachineName,
            OsPlatform = RuntimeInformation.OSDescription,
            GpuInfo = DetectHostGpu(),
            TotalMemoryMb = GetHostMemoryMb(),
            CpuCores = Environment.ProcessorCount,
            Containers = FilterRegisteredRuntimes(containers, allRegistered, ExecutionTarget.HostId).Select(ToContainerStatus).ToList(),
            Scripts = scripts
        };
    }

    /// <summary>
    /// Builds the host scripts list from registered runtimes where RuntimeKind==Script
    /// and Agent=="host". Each script's status is health-gated: process dead → "stopped",
    /// alive AND health Ready/Healthy → "running", alive otherwise → "starting".
    /// </summary>
    private async Task<IReadOnlyList<AgentScriptStatus>> BuildHostScriptsAsync(
        IReadOnlyList<RegisteredRuntime> allRegistered, CancellationToken ct)
    {
        var hostScripts = allRegistered
            .Where(r => r.RuntimeKind == RuntimeKind.Script
                && string.Equals(r.Agent, "host", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = new List<AgentScriptStatus>();
        foreach (var script in hostScripts)
        {
            var pid = _scriptController.GetProcessId(script.Id);
            var isAlive = pid.HasValue && _scriptController.IsScriptRunning(script.Id);

            string status;
            if (!isAlive)
            {
                status = "stopped";
            }
            else
            {
                // Health-gate: check if runtime is Ready/Healthy
                var isHealthy = script.Status == ContainerRegistrationStatus.Ready
                    || script.Status == ContainerRegistrationStatus.Healthy;
                if (!isHealthy && script.MappedPort.HasValue)
                {
                    try
                    {
                        isHealthy = await _healthChecker.CheckAsync(script.MappedPort.Value, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        isHealthy = false;
                    }
                }
                status = isHealthy ? "running" : "starting";
            }

            result.Add(new AgentScriptStatus
            {
                Path = script.LauncherPath ?? script.Image,
                PID = pid ?? 0,
                Status = status,
                Port = script.MappedPort ?? script.ContainerPort,
                StartTime = 0
            });
        }

        return result;
    }

    /// <summary>
    /// Applies the registered-container filter to an agent's telemetry containers.
    /// ContainerIds are matched case-insensitively against registered runtime ids;
    /// names are matched case-insensitively against registered images.
    /// For scripts: if agent telemetry reports "running" but the matching registered
    /// runtime (match by LauncherPath) is not Ready/Healthy, downgrade to "starting".
    /// </summary>
    private static AgentInfo FilterAgentContainers(AgentInfo info, IReadOnlyList<RegisteredRuntime> allRegistered)
    {
        var registry = new RegisteredContainerSet(allRegistered, info.Name);

        var filteredScripts = info.Scripts
            .Select(s =>
            {
                // Health-gate downgrade: if agent reports "running" but the matching
                // registered runtime is not Ready/Healthy, downgrade to "starting".
                if (string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    var matching = allRegistered.FirstOrDefault(r =>
                        r.RuntimeKind == RuntimeKind.Script
                        && string.Equals(r.Agent, info.Name, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(r.LauncherPath)
                        && string.Equals(r.LauncherPath, s.Path, StringComparison.OrdinalIgnoreCase));

                    if (matching is not null
                        && matching.Status != ContainerRegistrationStatus.Ready
                        && matching.Status != ContainerRegistrationStatus.Healthy)
                    {
                        return new AgentScriptStatus
                        {
                            Path = s.Path,
                            PID = s.PID,
                            Status = "starting",
                            Port = s.Port,
                            StartTime = s.StartTime
                        };
                    }
                }
                return s;
            })
            .ToList();

        return new AgentInfo
        {
            Name = info.Name,
            ConnectionId = info.ConnectionId,
            ConnectedAt = info.ConnectedAt,
            LastSeen = info.LastSeen,
            IsConnected = info.IsConnected,
            DockerSocket = info.DockerSocket,
            Version = info.Version,
            Hostname = info.Hostname,
            OsPlatform = info.OsPlatform,
            GpuInfo = info.GpuInfo,
            TotalMemoryMb = info.TotalMemoryMb,
            CpuCores = info.CpuCores,
            Containers = info.Containers
                .Where(c => registry.IsRegistered(c.ContainerId, c.ModelName))
                .ToList(),
            Scripts = filteredScripts
        };
    }

    /// <summary>
    /// Keeps only containers that belong to the registered set for the given agent.
    /// A container is kept if its Id matches a registered RuntimeContainerId, or if
    /// its ModelName/ModelId match a registered Image (container name).
    /// </summary>
    private static IReadOnlyList<ContainerInfo> FilterRegisteredRuntimes(
        IReadOnlyList<ContainerInfo> containers,
        IReadOnlyList<RegisteredRuntime> allRegistered,
        string agentName)
    {
        var registry = new RegisteredContainerSet(allRegistered, agentName);

        return containers
            .Where(c => registry.IsRegistered(c.Id, c.ModelName, c.ModelId, c.RegisteredRuntimeId))
            .ToList();
    }

    /// <summary>
    /// Case-insensitive lookup of the registered set for one agent.
    /// Runtime-container-id evidence (the registered-id link on the container, or a
    /// container id that equals a registered RuntimeContainerId) is authoritative and
    /// always honored. A name/image match alone is weaker evidence and is only used
    /// when no registered-id link is present AND the matched registration is not in
    /// Error status (an errored registration must not leak its container).
    /// </summary>
    private sealed class RegisteredContainerSet
    {
        private readonly HashSet<string> _runtimeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageNames = new(StringComparer.OrdinalIgnoreCase);
        // Map: registered container id (case-insensitive) → has registered runtime id.
        private readonly HashSet<string> _registeredWithRuntime = new(StringComparer.OrdinalIgnoreCase);

        public RegisteredContainerSet(IReadOnlyList<RegisteredRuntime> allRegistered, string agentName)
        {
            foreach (var registered in allRegistered)
            {
                if (!MatchesAgent(registered, agentName))
                    continue;

                if (!string.IsNullOrEmpty(registered.RuntimeContainerId))
                {
                    _runtimeIds.Add(registered.RuntimeContainerId);
                    _registeredWithRuntime.Add(registered.Id);
                }

                // Name/image matches are only honored for non-Error registrations.
                if (!string.IsNullOrEmpty(registered.Image) && registered.Status != ContainerRegistrationStatus.Error)
                    _imageNames.Add(registered.Image);
            }
        }

        /// <summary>Telemetry-style entry: runtime id and/or model name.</summary>
        public bool IsRegistered(string? containerId, string? modelName)
        {
            if (!string.IsNullOrEmpty(containerId) && _runtimeIds.Contains(containerId))
                return true;

            // No runtime-id evidence: fall back to the image name (non-Error only).
            return !string.IsNullOrEmpty(modelName) && _imageNames.Contains(modelName);
        }

        /// <summary>Host-list style entry: runtime id, model name/model id, and optional registry link.</summary>
        public bool IsRegistered(string? containerId, string? modelName, string? modelId, string? registeredRuntimeId)
        {
            // Preferred: the container carries its registered-container link — that is
            // authoritative evidence the container is managed by this agent.
            if (!string.IsNullOrEmpty(registeredRuntimeId))
                return _registeredWithRuntime.Contains(registeredRuntimeId);

            return IsRegistered(containerId, modelName)
                || (!string.IsNullOrEmpty(modelId) && _imageNames.Contains(modelId));
        }

        private static bool MatchesAgent(RegisteredRuntime registered, string agentName)
        {
            if (string.IsNullOrWhiteSpace(registered.Agent))
                return string.Equals(agentName, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);
            return string.Equals(registered.Agent, agentName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static AgentContainerStatus ToContainerStatus(ContainerInfo container) => new()
    {
        ContainerId = container.Id,
        ModelName = string.IsNullOrEmpty(container.ModelName) ? null : container.ModelName,
        Status = container.Status.ToString().ToLowerInvariant(),
        Port = container.Port
    };

    private static string? DetectHostGpu()
    {
        try
        {
            var entries = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? DetectGpuWindows()
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? DetectGpuMacOs()
                    : DetectGpuLinux();

            if (entries.Count == 0)
                return null;

            // "NVIDIA GeForce RTX 4090 (24 GB), AMD Radeon Pro VII (16 GB)"
            return string.Join(", ", entries);
        }
        catch
        {
            return null;
        }
    }

    // ── Linux: lspci for names, sysfs / nvidia-smi for VRAM ────────

    private static List<string> DetectGpuLinux()
    {
        var results = new List<string>();

        // GPU names via lspci (works for all vendors)
        var lspciOutput = RunTool("lspci", "-nn");
        if (string.IsNullOrEmpty(lspciOutput))
            return results;

        foreach (var line in lspciOutput.Split('\n'))
        {
            if (!line.Contains("VGA", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("3D", StringComparison.OrdinalIgnoreCase))
                continue;

            // "05:00.0 VGA compatible controller [0300]: AMD ... [1002:66a1] (rev 06)"
            // or  "01:00.0 VGA compatible controller: NVIDIA Corporation GeForce RTX 4090 (rev a1)"
            var name = ExtractLinuxGpuName(line);
            if (string.IsNullOrEmpty(name))
                continue;

            var vramMb = GetLinuxGpuVram(results.Count);
            if (vramMb > 0)
                results.Add($"{name} ({vramMb / 1024} GB)");
            else
                results.Add(name);
        }

        // Fallback: if lspci found nothing, try nvidia-smi (NVIDIA-only)
        if (results.Count == 0)
        {
            var nvidiaOutput = RunTool("nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader,nounits");
            if (!string.IsNullOrEmpty(nvidiaOutput))
            {
                foreach (var line in nvidiaOutput.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    // "NVIDIA GeForce RTX 4090, 24564" -> "NVIDIA GeForce RTX 4090 (24 GB)"
                    var parts = trimmed.Split(',', 2);
                    if (parts.Length == 2 &&
                        long.TryParse(parts[1].Trim(), out var mb))
                        results.Add($"{parts[0].Trim()} ({mb / 1024} GB)");
                    else
                        results.Add(trimmed);
                }
            }
        }

        return results;
    }

    private static string ExtractLinuxGpuName(string lspciLine)
    {
        // "05:00.0 VGA compatible controller [0300]: Advanced Micro Devices, Inc. [AMD/ATI] Vega 20 [Radeon Pro VII/Radeon Instinct MI50] [1002:66a1] (rev 06)"
        // → "Radeon Pro VII"
        //
        // "01:00.0 VGA compatible controller: NVIDIA Corporation GeForce RTX 4090 (rev a1)"
        // → "GeForce RTX 4090"

        var name = lspciLine;

        // Strip leading address (everything up to and including the first space after the address)
        // "05:00.0 VGA..." or "46:00.0 VGA..." → "VGA..."
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"^\S+\s+", "").Trim();

        // Strip controller type keywords
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"VGA compatible controller:?\s*|3D controller:?\s*|Display controller:?\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Strip trailing "(rev xx)"
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"\s*\(rev\s+\w+\)\s*$", "").Trim();

        // Iterate bracket contents from last to first — find the first non-PCI-ID bracket.
        // The marketing name lives inside the last meaningful brackets.
        var allBrackets = System.Text.RegularExpressions.Regex.Matches(name, @"\[([^\]]+)\]");
        for (int i = allBrackets.Count - 1; i >= 0; i--)
        {
            var content = allBrackets[i].Value[1..^1].Trim(); // strip [ and ]

            // Skip PCI device ID brackets like "[1002:66a1]" or "[0300]"
            if (System.Text.RegularExpressions.Regex.IsMatch(content, @"^[0-9a-f]{4}(:[0-9a-f]{4})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            // "Radeon Pro VII/Radeon Instinct MI50" → "Radeon Pro VII"
            var slashIdx = content.IndexOf('/');
            if (slashIdx > 0)
                content = content[..slashIdx].Trim();

            return content;
        }

        // No useful brackets — strip known vendor prefixes
        // "NVIDIA Corporation GeForce RTX 4090" → "GeForce RTX 4090"
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"^(NVIDIA Corporation|Advanced Micro Devices, Inc\.|Intel Corporation|Qualcomm Technologies, Inc\.|Broadcom Inc\.|VMware, Inc\.|Xilinx, Inc\.|Motorola|Matrox)\s*",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        return string.IsNullOrEmpty(name) ? null! : name;
    }

    private static long GetLinuxGpuVram(int gpuIndex)
    {
        // Try sysfs per-card VRAM (AMD, Intel Arc, NVIDIA open drivers)
        try
        {
            var cards = System.IO.Directory.GetDirectories("/sys/class/drm", "card*")
                .Where(d => System.IO.File.Exists(System.IO.Path.Combine(d, "device", "mem_info_vram_total")))
                .OrderBy(d => d)
                .ToList();

            if (gpuIndex < cards.Count)
            {
                var vramPath = System.IO.Path.Combine(cards[gpuIndex], "device", "mem_info_vram_total");
                var text = System.IO.File.ReadAllText(vramPath).Trim();
                if (long.TryParse(text, out var bytes) && bytes > 0)
                    return bytes / (1024 * 1024); // bytes → MB
            }
        }
        catch { /* sysfs unavailable */ }

        // Try nvidia-smi for NVIDIA VRAM
        try
        {
            var output = RunTool("nvidia-smi",
                "--query-gpu=memory.total --format=csv,noheader,nounits");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (gpuIndex < lines.Length &&
                    long.TryParse(lines[gpuIndex].Trim(), out var mb) && mb > 0)
                    return mb;
            }
        }
        catch { /* nvidia-smi unavailable */ }

        return 0;
    }

    // ── Windows: WMI for name, registry for accurate VRAM ──────────

    private static List<string> DetectGpuWindows()
    {
        var results = new List<string>();

        try
        {
            // WMI gives us GPU names
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command \"Get-CimInstance Win32_VideoController | Select-Object Name,PNPDeviceID | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return results;
            var json = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            if (string.IsNullOrWhiteSpace(json))
                return results;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Single object or array
            var devices = root.ValueKind == System.Text.Json.JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : [root];

            foreach (var device in devices)
            {
                var name = device.GetProperty("Name").GetString();
                var pnpId = device.TryGetProperty("PNPDeviceID", out var pnp) ? pnp.GetString() : null;

                // Try to get VRAM from registry (accurate 64-bit value)
                var vramMb = GetWindowsVramFromRegistry(pnpId);
                if (vramMb > 0)
                    results.Add($"{name} ({vramMb / 1024} GB)");
                else
                    results.Add(name ?? "Unknown GPU");
            }
        }
        catch { /* PowerShell unavailable or WMI error */ }

        return results;
    }

    private static long GetWindowsVramFromRegistry(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId))
            return 0;

        try
        {
            // PNPDeviceID like "PCI\VEN_10DE&DEV_2684&SUBSYS_..." → extract VEN/DEV
            var match = System.Text.RegularExpressions.Regex.Match(pnpDeviceId,
                @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                return 0;

            var vendorId = match.Groups[1].Value.ToUpperInvariant();
            var classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(classKey);
            if (baseKey is null) return 0;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                if (!subKeyName.StartsWith("0", StringComparison.Ordinal) ||
                    subKeyName.Length > 4) // Only 0000, 0001, etc.
                    continue;

                using var subKey = baseKey.OpenSubKey(subKeyName);
                if (subKey is null) continue;

                var hwVendor = subKey.GetValue("HardwareInformation.AdapterString")?.ToString() ?? "";
                var regPnp = subKey.GetValue("MatchingDeviceId")?.ToString() ?? "";

                // Match by PNP ID substring
                if (!string.IsNullOrEmpty(regPnp) &&
                    pnpDeviceId.Contains(regPnp, StringComparison.OrdinalIgnoreCase))
                {
                    // qwMemorySize is a QWORD (64-bit) with accurate VRAM in bytes
                    var val = subKey.GetValue("HardwareInformation.qwMemorySize");
                    if (val is long bytes && bytes > 0)
                        return bytes / (1024 * 1024);

                    // Fallback: AdapterRAM (uint32, capped at 4GB)
                    var adapterRam = subKey.GetValue("AdapterRAM");
                    if (adapterRam is int ram && ram > 0)
                        return ram / (1024 * 1024);
                }
            }
        }
        catch { /* registry access denied or unavailable */ }

        return 0;
    }

    // ── macOS: system_profiler ──────────────────────────────────────

    private static List<string> DetectGpuMacOs()
    {
        var results = new List<string>();

        var output = RunTool("system_profiler", "SPDisplaysDataType");
        if (string.IsNullOrEmpty(output))
            return results;

        string? currentName = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();

            // "Chipset Model: AMD Radeon Pro VII"
            if (trimmed.StartsWith("Chipset Model:", StringComparison.OrdinalIgnoreCase))
            {
                currentName = trimmed["Chipset Model:".Length..].Trim();
            }
            // "VRAM (Total): 16 GB" or "Memory: 16 GB"
            else if (currentName is not null &&
                     (trimmed.StartsWith("VRAM (Total):", StringComparison.OrdinalIgnoreCase) ||
                      trimmed.StartsWith("Memory:", StringComparison.OrdinalIgnoreCase)))
            {
                var vramStr = trimmed.Contains(':')
                    ? trimmed[(trimmed.IndexOf(':') + 1)..].Trim()
                    : "";

                results.Add(string.IsNullOrEmpty(vramStr)
                    ? currentName
                    : $"{currentName} ({vramStr})");
                currentName = null;
            }
        }

        // Handle case where name was found but no VRAM line followed
        if (currentName is not null)
            results.Add(currentName);

        return results;
    }

    // ── Shared helper ───────────────────────────────────────────────

    private static string? RunTool(string command, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static long GetHostMemoryMb()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && System.IO.File.Exists("/proc/meminfo"))
            {
                foreach (var line in System.IO.File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                            return kb / 1024;
                    }
                }
            }
            // Fallback: use GC memory info
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch
        {
            return 0;
        }
    }
}
