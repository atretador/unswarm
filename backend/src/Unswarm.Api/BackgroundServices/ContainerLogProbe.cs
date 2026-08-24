using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.BackgroundServices;

/// <summary>
/// Background probe that polls container and script logs every few seconds and
/// enqueues new lines into <see cref="ILogStore"/> so the Logs page shows real data.
///
/// Container logs: fetches the last N lines from each registered runtime's Docker
/// controller (host or remote agent) and enqueues lines not seen in the previous poll.
///
/// Script logs: tails log files written by <see cref="HostScriptRuntimeController"/>
/// and enqueues new lines (source = script's DisplayName).
/// </summary>
public sealed class ContainerLogProbe : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ContainerLogProbe> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int ContainerTailLines = 100;
    private const int ScriptTailLines = 100;

    /// <summary>
    /// Per-container dedup: maps RuntimeContainerId → lines from the previous poll.
    /// New lines = current lines minus previous lines. Bounded by the tail window.
    /// </summary>
    private readonly Dictionary<string, string[]> _lastContainerLines = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-script dedup: maps regId → lines from the previous poll (same strategy).
    /// </summary>
    private readonly Dictionary<string, string[]> _lastScriptLines = new(StringComparer.Ordinal);

    public ContainerLogProbe(IServiceProvider services, ILogger<ContainerLogProbe> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ContainerLogProbe started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollContainerLogsAsync(stoppingToken).ConfigureAwait(false);
                await PollScriptLogsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ContainerLogProbe poll error");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ContainerLogProbe stopped");
    }

    private async Task PollContainerLogsAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IContainerRegistry>();
        var logStore = scope.ServiceProvider.GetRequiredService<ILogStore>();
        var router = scope.ServiceProvider.GetRequiredService<IDockerControllerRouter>();

        var runtimes = await registry.ListAllAsync(ct).ConfigureAwait(false);

        foreach (var runtime in runtimes.Where(r =>
            r.RuntimeKind == RuntimeKind.Container &&
            r.RuntimeContainerId is not null))
        {
            try
            {
                // "host"/empty agent → local Docker target; anything else → agent:<name>
                var isHost = string.IsNullOrWhiteSpace(runtime.Agent)
                    || string.Equals(runtime.Agent, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);
                var targetId = isHost ? ExecutionTarget.HostId : ExecutionTarget.ForAgent(runtime.Agent!).Id;
                if (!router.IsTargetReachable(targetId))
                    continue;

                var controller = router.GetController(targetId);
                var lines = await controller.GetContainerLogsAsync(
                    runtime.RuntimeContainerId!, ContainerTailLines, ct).ConfigureAwait(false);

                var currentLines = lines.ToArray();

                _lastContainerLines.TryGetValue(runtime.RuntimeContainerId!, out var previousLines);
                foreach (var line in DiffNewLines(previousLines, currentLines))
                {
                    var level = ClassifyContainerLogLine(line);
                    logStore.Enqueue(level, runtime.DisplayName, line);
                }

                _lastContainerLines[runtime.RuntimeContainerId!] = currentLines;
            }
            catch (Exception ex)
            {
                // Skip unreachable targets quietly — agent may be offline or container stopped
                _logger.LogDebug(ex, "Failed to get container logs for runtime {RuntimeId} ({DisplayName})",
                    runtime.Id, runtime.DisplayName);
            }
        }
    }

    private async Task PollScriptLogsAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IContainerRegistry>();
        var logStore = scope.ServiceProvider.GetRequiredService<ILogStore>();
        var scriptController = scope.ServiceProvider.GetRequiredService<HostScriptRuntimeController>();

        var runtimes = await registry.ListAllAsync(ct).ConfigureAwait(false);
        var scriptRuntimes = runtimes.Where(r =>
            r.RuntimeKind == RuntimeKind.Script &&
            r.RuntimeProcessId is not null &&
            scriptController.IsScriptRunning(r.Id)).ToList();

        foreach (var runtime in scriptRuntimes)
        {
            try
            {
                var lines = await scriptController.GetScriptLogsAsync(
                    runtime.Id, ScriptTailLines, ct).ConfigureAwait(false);

                var currentLines = lines.ToArray();

                _lastScriptLines.TryGetValue(runtime.Id, out var previousScriptLines);
                foreach (var line in DiffNewLines(previousScriptLines, currentLines))
                {
                    var level = ClassifyScriptLogLine(line);
                    logStore.Enqueue(level, runtime.DisplayName, line);
                }

                _lastScriptLines[runtime.Id] = currentLines;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get script logs for runtime {RuntimeId} ({DisplayName})",
                    runtime.Id, runtime.DisplayName);
            }
        }

        // Clean up tracking for scripts that are no longer running
        var runningIds = new HashSet<string>(scriptRuntimes.Select(r => r.Id), StringComparer.Ordinal);
        foreach (var staleId in _lastScriptLines.Keys.Where(k => !runningIds.Contains(k)).ToList())
        {
            _lastScriptLines.Remove(staleId);
        }
    }

    /// <summary>
    /// Cursor-based tail diff: when the previous poll's lines are a contiguous
    /// suffix of the current tail (the common case — the log only grew), only the
    /// appended lines after that suffix are new. This never re-enqueues overlapping
    /// lines, even when identical lines repeat within the window (a set-diff would
    /// misclassify duplicates as new). When continuity is broken (log rotation,
    /// truncation, or more growth than the tail window), falls back to a set-diff.
    /// </summary>
    private static List<string> DiffNewLines(string[]? previousLines, string[] currentLines)
    {
        if (previousLines is null || previousLines.Length == 0)
            return currentLines.ToList(); // first poll — full initial history

        if (currentLines.Length >= previousLines.Length)
        {
            var overlap = previousLines.AsSpan();
            var candidate = currentLines.AsSpan(currentLines.Length - previousLines.Length);
            if (overlap.SequenceEqual(candidate))
                return currentLines.Take(currentLines.Length - previousLines.Length).ToList();
        }

        var previousSet = new HashSet<string>(previousLines);
        return currentLines.Where(line => !previousSet.Contains(line)).ToList();
    }

    /// <summary>
    /// Heuristic level classification for container log lines.
    /// The Docker logs API mixes stdout/stderr into a single stream; the stream
    /// identity is only available via the multiplexed header byte, which the
    /// current Docker.DotNet wrapper does not expose. We use keyword heuristics.
    /// </summary>
    private static LogLevel ClassifyContainerLogLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return LogLevel.Info;

        // Common error indicators (case-insensitive match against suffix/keywords)
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("panic", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("OOM", StringComparison.Ordinal) ||
            line.Contains("killed", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;

        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("deprecated", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("slow", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warn;

        return LogLevel.Info;
    }

    /// <summary>
    /// Level classification for host script log lines.
    /// Script logs have [stdout] / [stderr] prefixes written by HostScriptRuntimeController.
    /// </summary>
    private static LogLevel ClassifyScriptLogLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return LogLevel.Info;

        if (line.StartsWith("[stderr]", StringComparison.Ordinal))
            return LogLevel.Warn;

        return LogLevel.Info;
    }
}
