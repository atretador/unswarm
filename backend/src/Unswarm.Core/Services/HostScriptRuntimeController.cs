using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Unswarm.Core.Services;

/// <summary>
/// Host-only script process manager. Spawns/monitors/kills launcher scripts.
/// Holds in-memory process state (PID, start time). Singleton lifetime.
/// PID file strategy: write &lt;dataDir&gt;/script-logs/&lt;regId&gt;.pid for orphan detection.
/// Logs: &lt;dataDir&gt;/script-logs/&lt;regId&gt;.log (stdout+stderr combined).
/// </summary>
public sealed class HostScriptRuntimeController
{
    private readonly ILogger<HostScriptRuntimeController> _logger;
    private readonly string _scriptLogsDir;
    private readonly ConcurrentDictionary<string, ScriptProcessInfo> _processes = new(StringComparer.Ordinal);

    public HostScriptRuntimeController(ILogger<HostScriptRuntimeController> logger, string? dataDir = null)
    {
        _logger = logger;
        var baseDir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "unswarm");
        _scriptLogsDir = Path.Combine(baseDir, "script-logs");
        Directory.CreateDirectory(_scriptLogsDir);
    }

    public sealed record ScriptStartResult
    {
        public int? Pid { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private sealed class ScriptProcessInfo
    {
        public required int Pid { get; init; }
        public required DateTimeOffset StartTime { get; init; }
        public required Process Process { get; init; }
        public required StreamWriter LogWriter { get; init; }
    }

    public async Task<ScriptStartResult> StartScriptAsync(string regId, string launcherPath, int declaredPort, CancellationToken ct = default)
    {
        // Duplicate guard: return success with existing PID if already running.
        if (_processes.TryGetValue(regId, out var existing))
        {
            try
            {
                if (!existing.Process.HasExited)
                {
                    return new ScriptStartResult { Pid = existing.Pid };
                }
            }
            catch
            {
                // Process info stale; remove and continue
            }
            _processes.TryRemove(regId, out _);
        }

        if (string.IsNullOrWhiteSpace(launcherPath))
        {
            return new ScriptStartResult { ErrorMessage = "LauncherPath is empty" };
        }

        if (!File.Exists(launcherPath))
        {
            return new ScriptStartResult { ErrorMessage = $"Launcher script not found: {launcherPath}" };
        }

        var logFile = Path.Combine(_scriptLogsDir, $"{regId}.log");
        var pidFile = Path.Combine(_scriptLogsDir, $"{regId}.pid");

        try
        {
            var workingDir = Path.GetDirectoryName(launcherPath) ?? Path.GetTempPath();
            var logWriter = new StreamWriter(logFile, append: false) { AutoFlush = true };

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // --login sources /etc/profile + ~/.profile so scripts inherit the
            // user's PATH and environment (e.g. llama-server in ~/.local/bin).
            psi.ArgumentList.Add("--login");
            psi.ArgumentList.Add(launcherPath);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    try { logWriter.WriteLine($"[stdout] {e.Data}"); } catch { }
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    try { logWriter.WriteLine($"[stderr] {e.Data}"); } catch { }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var pid = process.Id;
            var info = new ScriptProcessInfo
            {
                Pid = pid,
                StartTime = DateTimeOffset.UtcNow,
                Process = process,
                LogWriter = logWriter
            };
            _processes[regId] = info;

            // Write PID file for orphan detection
            try
            {
                await File.WriteAllTextAsync(pidFile, pid.ToString(), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write PID file for script {RegId}", regId);
            }

            _logger.LogInformation("Started script runtime {RegId} (PID {Pid}, launcher: {Launcher})",
                regId, pid, launcherPath);

            return new ScriptStartResult { Pid = pid };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start script runtime {RegId} (launcher: {Launcher})",
                regId, launcherPath);
            return new ScriptStartResult { ErrorMessage = $"Failed to start script: {ex.Message}" };
        }
    }

    public async Task StopScriptAsync(string regId, CancellationToken ct = default)
    {
        if (!_processes.TryRemove(regId, out var info))
        {
            _logger.LogWarning("Script runtime {RegId} not found in tracking; cleaning up PID file", regId);
            CleanupPidFile(regId);
            return;
        }

        try
        {
            if (!info.Process.HasExited)
            {
                _logger.LogInformation("Stopping script runtime {RegId} (PID {Pid})", regId, info.Pid);

                // Graceful: send SIGTERM to the process group
                try
                {
                    using var sigterm = Process.Start("kill", $"-TERM -- -{info.Pid}");
                    sigterm?.WaitForExit(2000);
                }
                catch
                {
                    // kill command unavailable or failed; fall through to force
                }

                // Wait up to 5 seconds for graceful exit
                var exited = await Task.Run(() => info.Process.WaitForExit(5000), ct).ConfigureAwait(false);
                if (!exited)
                {
                    _logger.LogWarning("Script runtime {RegId} (PID {Pid}) did not exit within grace period; force killing", regId, info.Pid);
                    try
                    {
                        info.Process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Already exited or cannot kill
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping script runtime {RegId} (PID {Pid})", regId, info.Pid);
        }
        finally
        {
            try { info.LogWriter.Dispose(); } catch { }
            try { info.Process.Dispose(); } catch { }
            CleanupPidFile(regId);
        }
    }

    public bool IsScriptRunning(string regId)
    {
        if (!_processes.TryGetValue(regId, out var info))
            return false;

        try
        {
            return !info.Process.HasExited;
        }
        catch
        {
            _processes.TryRemove(regId, out _);
            return false;
        }
    }

    /// <summary>
    /// Checks if any tracked script is using the given launcher path.
    /// Used to prevent deletion of scripts that are currently running.
    /// </summary>
    public bool IsRunningByPath(string launcherPath)
    {
        var full = Path.GetFullPath(launcherPath);
        foreach (var kvp in _processes)
        {
            try
            {
                if (!kvp.Value.Process.HasExited &&
                    string.Equals(kvp.Value.Process.StartInfo.ArgumentList.FirstOrDefault(), full, StringComparison.Ordinal))
                    return true;
            }
            catch
            {
                // Stale entry; will be pruned on next status check
            }
        }
        return false;
    }

    public int? GetProcessId(string regId)
    {
        if (!_processes.TryGetValue(regId, out var info))
            return null;

        try
        {
            return info.Process.HasExited ? null : info.Pid;
        }
        catch
        {
            _processes.TryRemove(regId, out _);
            return null;
        }
    }

    public TimeSpan? GetUptime(string regId)
    {
        if (!_processes.TryGetValue(regId, out var info))
            return null;

        try
        {
            if (info.Process.HasExited)
                return null;
            return DateTimeOffset.UtcNow - info.StartTime;
        }
        catch
        {
            _processes.TryRemove(regId, out _);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> GetScriptLogsAsync(string regId, int tailLines = 100, CancellationToken ct = default)
    {
        var logFile = Path.Combine(_scriptLogsDir, $"{regId}.log");
        if (!File.Exists(logFile))
            return [];

        try
        {
            var allLines = await File.ReadAllLinesAsync(logFile, ct).ConfigureAwait(false);
            return allLines.TakeLast(tailLines).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read script logs for {RegId}", regId);
            return [];
        }
    }

    /// <summary>
    /// Scans the script-logs directory for .pid files. For each: if the PID is alive,
    /// the process is adopted (tracked in-memory); if dead, the pid file is cleaned up.
    /// Called once at startup.
    /// </summary>
    public Task AdoptOrphanedScriptsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_scriptLogsDir))
            return Task.CompletedTask;

        foreach (var pidFile in Directory.EnumerateFiles(_scriptLogsDir, "*.pid"))
        {
            ct.ThrowIfCancellationRequested();

            var regId = Path.GetFileNameWithoutExtension(pidFile);
            try
            {
                var pidText = File.ReadAllText(pidFile).Trim();
                if (!int.TryParse(pidText, out var pid))
                {
                    _logger.LogWarning("Invalid PID file {PidFile}; cleaning up", pidFile);
                    TryDeleteFile(pidFile);
                    continue;
                }

                var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    _logger.LogInformation("Orphaned script {RegId} (PID {Pid}) has exited; cleaning up", regId, pid);
                    TryDeleteFile(pidFile);
                    TryDeleteFile(Path.Combine(_scriptLogsDir, $"{regId}.log"));
                    process.Dispose();
                    continue;
                }

                // Adopt the running process
                _logger.LogInformation("Adopted orphaned script {RegId} (PID {Pid})", regId, pid);
                var logFile = Path.Combine(_scriptLogsDir, $"{regId}.log");
                var logWriter = new StreamWriter(logFile, append: true) { AutoFlush = true };
                _processes[regId] = new ScriptProcessInfo
                {
                    Pid = pid,
                    StartTime = process.StartTime, // best effort from OS
                    Process = process,
                    LogWriter = logWriter
                };
            }
            catch (ArgumentException)
            {
                // Process not found — dead
                _logger.LogInformation("Orphaned script {RegId} PID not alive; cleaning up", regId);
                TryDeleteFile(pidFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing orphan PID file {PidFile}", pidFile);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the list of script regIds currently tracked as running.
    /// </summary>
    public IReadOnlyList<string> GetRunningScriptIds()
    {
        return _processes
            .Where(kv =>
            {
                try { return !kv.Value.Process.HasExited; }
                catch { return false; }
            })
            .Select(kv => kv.Key)
            .ToList();
    }

    private void CleanupPidFile(string regId)
    {
        TryDeleteFile(Path.Combine(_scriptLogsDir, $"{regId}.pid"));
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
