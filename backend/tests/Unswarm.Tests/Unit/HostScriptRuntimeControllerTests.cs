using Microsoft.Extensions.Logging;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class HostScriptRuntimeControllerTests : IAsyncLifetime
{
    private readonly string _testDir;
    private readonly HostScriptRuntimeController _controller;
    private readonly List<string> _scriptFiles = [];

    public HostScriptRuntimeControllerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"unswarm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _controller = new HostScriptRuntimeController(
            new LoggerFactory().CreateLogger<HostScriptRuntimeController>(),
            _testDir);
    }

    public async Task InitializeAsync()
    {
        await _controller.AdoptOrphanedScriptsAsync();
    }

    public Task DisposeAsync()
    {
        // Clean up any running scripts
        foreach (var regId in _controller.GetRunningScriptIds())
        {
            try { _controller.StopScriptAsync(regId).Wait(TimeSpan.FromSeconds(5)); } catch { }
        }

        try { Directory.Delete(_testDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private string CreateScript(string content)
    {
        var path = Path.Combine(_testDir, $"script-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/bash\n{content}");
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        _scriptFiles.Add(path);
        return path;
    }

    [Fact]
    public async Task StartScriptAsync_SimpleLoop_ProcessRunning()
    {
        var script = CreateScript("while true; do sleep 1; done");

        var result = await _controller.StartScriptAsync("test-loop", script, 8080);

        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.Pid);
        Assert.True(result.Pid > 0);
        Assert.True(_controller.IsScriptRunning("test-loop"));
        Assert.Equal(result.Pid, _controller.GetProcessId("test-loop"));
        Assert.NotNull(_controller.GetUptime("test-loop"));

        await _controller.StopScriptAsync("test-loop");
        Assert.False(_controller.IsScriptRunning("test-loop"));
    }

    [Fact]
    public async Task StartScriptAsync_DuplicateGuard_IdempotentSuccess()
    {
        var script = CreateScript("while true; do sleep 1; done");

        var result1 = await _controller.StartScriptAsync("test-dup", script, 8080);
        Assert.Null(result1.ErrorMessage);
        Assert.NotNull(result1.Pid);

        var result2 = await _controller.StartScriptAsync("test-dup", script, 8081);
        Assert.Null(result2.ErrorMessage);
        Assert.NotNull(result2.Pid);
        Assert.Equal(result1.Pid, result2.Pid);

        await _controller.StopScriptAsync("test-dup");
    }

    [Fact]
    public async Task StartScriptAsync_ScriptExits_IsRunningReturnsFalse()
    {
        // Script that exits immediately
        var script = CreateScript("echo done && exit 0");

        var result = await _controller.StartScriptAsync("test-exit", script, 8080);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.Pid);

        // Wait for script to exit
        await Task.Delay(500);

        Assert.False(_controller.IsScriptRunning("test-exit"));
        Assert.Null(_controller.GetProcessId("test-exit"));
        Assert.Null(_controller.GetUptime("test-exit"));
    }

    [Fact]
    public async Task GetScriptLogsAsync_CapturesStdout()
    {
        var script = CreateScript("echo hello-world && sleep 30");

        var result = await _controller.StartScriptAsync("test-logs", script, 8080);
        Assert.Null(result.ErrorMessage);

        // Wait for output to flush
        await Task.Delay(500);

        var logs = await _controller.GetScriptLogsAsync("test-logs");
        Assert.Contains(logs, l => l.Contains("hello-world"));

        await _controller.StopScriptAsync("test-logs");
    }

    [Fact]
    public async Task StartScriptAsync_FileNotFound_ReturnsError()
    {
        var result = await _controller.StartScriptAsync("test-nofile", "/nonexistent/path/script.sh", 8080);

        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not found", result.ErrorMessage);
        Assert.Null(result.Pid);
    }

    [Fact]
    public async Task StartScriptAsync_EmptyLauncherPath_ReturnsError()
    {
        var result = await _controller.StartScriptAsync("test-empty", "", 8080);

        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Pid);
    }

    [Fact]
    public async Task StopScriptAsync_NotRunning_NoThrow()
    {
        // Should not throw for a non-running script
        await _controller.StopScriptAsync("nonexistent");
    }

    [Fact]
    public async Task GetScriptLogsAsync_MissingFile_ReturnsEmpty()
    {
        var logs = await _controller.GetScriptLogsAsync("nonexistent-log");
        Assert.Empty(logs);
    }

    [Fact]
    public async Task StartScriptAsync_SetsEnvironmentVariables()
    {
        // Script that writes env vars to a file
        var envFile = Path.Combine(_testDir, $"env-{Guid.NewGuid():N}.txt");
        var script = CreateScript($"echo PORT=$UNSWARM_PORT REG=$UNSWARM_REG_ID > {envFile} && sleep 30");

        var result = await _controller.StartScriptAsync("test-env", script, 9090);
        Assert.Null(result.ErrorMessage);

        await Task.Delay(500);

        var envContent = await File.ReadAllTextAsync(envFile);
        Assert.Contains("PORT=9090", envContent);
        Assert.Contains("REG=test-env", envContent);

        await _controller.StopScriptAsync("test-env");
    }

    [Fact]
    public async Task AdoptOrphanedScriptsAsync_CleansDeadPidFiles()
    {
        var scriptLogsDir = Path.Combine(_testDir, "script-logs");
        Directory.CreateDirectory(scriptLogsDir);

        // Write a stale PID file for a process that doesn't exist
        var pidFile = Path.Combine(scriptLogsDir, "dead-script.pid");
        await File.WriteAllTextAsync(pidFile, "99999999");

        await _controller.AdoptOrphanedScriptsAsync();

        // The stale PID file should be cleaned up
        Assert.False(File.Exists(pidFile));
    }
}
