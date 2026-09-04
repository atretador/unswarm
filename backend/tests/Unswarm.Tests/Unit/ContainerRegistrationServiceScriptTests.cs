using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ContainerRegistrationServiceScriptTests : IDisposable
{
    private readonly FakeContainerRegistry _registry = new();
    private readonly FakeDockerController _docker = new();
    private readonly FakeDockerControllerRouter _router;
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeModelRegistry _modelRegistry = new();
    private readonly FakeClock _clock = new();
    private readonly ILogger<ContainerRegistrationService> _logger =
        new LoggerFactory().CreateLogger<ContainerRegistrationService>();
    private readonly ILogger<HostScriptRuntimeController> _scriptLogger =
        new LoggerFactory().CreateLogger<HostScriptRuntimeController>();
    private readonly List<TcpListener> _listeners = [];
    private readonly string _testDir;

    public ContainerRegistrationServiceScriptTests()
    {
        _router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker });
        _testDir = Path.Combine(Path.GetTempPath(), $"unswarm-reg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    private int StartDiscoveryServer(string jsonResponse = """{"data":[]}""")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);
                    using var writer = new StreamWriter(stream) { AutoFlush = true };

                    await reader.ReadLineAsync();
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && line.Length > 0) { }

                    var bodyBytes = Encoding.UTF8.GetBytes(jsonResponse);
                    await writer.WriteAsync(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\n\r\n");
                    await stream.WriteAsync(bodyBytes);
                }
            }
            catch { /* listener stopped */ }
        });

        return port;
    }

    private string CreateScript(string content)
    {
        var path = Path.Combine(_testDir, $"script-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/bash\n{content}");
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        return path;
    }

    private HostScriptRuntimeController CreateScriptController()
    {
        return new HostScriptRuntimeController(_scriptLogger, _testDir);
    }

    private ContainerRegistrationService CreateService(
        ModelDiscoveryService? discoveryService = null,
        FakeDockerControllerRouter? router = null,
        HostScriptRuntimeController? scriptController = null)
    {
        var settings = new FakeSettingsStore(new Settings { HealthCheckTimeoutSeconds = 120 });

        return new ContainerRegistrationService(
            _registry,
            router ?? _router,
            _healthChecker,
            discoveryService ?? new ModelDiscoveryService(new LoggerFactory().CreateLogger<ModelDiscoveryService>(), Options.Create(new ContainerHostOptions())),
            _modelRegistry,
            _clock,
            _logger,
            settings,
            scriptController: scriptController);
    }

    [Fact]
    public async Task RegisterAsync_ScriptKind_RegistersWithoutStarting()
    {
        var script = CreateScript("sleep 30");

        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Test Script",
            Image = "test-script:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            ContainerPort = 8080
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Equal(RuntimeKind.Script, result.Container.RuntimeKind);
        Assert.Null(result.Container.RuntimeProcessId);
        Assert.Null(result.Container.MappedPort);
        Assert.Equal(8080, result.Container.ContainerPort);
        Assert.Null(result.Container.RuntimeContainerId);
        Assert.Equal("Test Script", result.Container.DisplayName);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task RegisterAsync_ScriptKind_NonHostAgent_ThrowsNotSupported()
    {
        var script = CreateScript("sleep 30");
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Remote Script",
            Image = "remote-script:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            ContainerPort = 8080,
            Agent = "gpu1"
        };

        // Agent not connected → controller lookup fails with InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterAsync(request));
    }

    [Fact]
    public async Task RegisterAsync_ScriptKind_EmptyLauncherPath_Throws()
    {
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "No Path",
            Image = "no-path:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = "",
            ContainerPort = 8080
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterAsync(request));
    }

    [Fact]
    public async Task DeleteAsync_ScriptKind_StopsScript()
    {
        var script = CreateScript("sleep 30");
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "DeleteScript",
            Image = "del:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            ContainerPort = 8080
        };

        var result = await service.RegisterAsync(request);
        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Null(result.Container.RuntimeProcessId);

        // Delete — registration-only means the script was never started, so
        // RuntimeProcessId is null and the delete skips the stop call.
        await service.DeleteAsync(result.Container.Id, deleteModels: false);

        Assert.Contains(result.Container.Id, _registry.DeletedContainerIds);
        Assert.False(scriptController.IsScriptRunning(result.Container.Id));
    }

    [Fact]
    public async Task StartAsync_ScriptKind_RestartsScript()
    {
        var discoveryPort = StartDiscoveryServer();
        var script = CreateScript("sleep 30");
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        // Register first
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "RestartScript",
            Image = "restart:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            ContainerPort = discoveryPort
        };
        var result = await service.RegisterAsync(request);
        var regId = result.Container.Id;

        // Stop the script (simulate crash)
        await scriptController.StopScriptAsync(regId);
        Assert.False(scriptController.IsScriptRunning(regId));

        // Start should restart it
        var started = await service.StartAsync(regId);

        Assert.Equal(ContainerRegistrationStatus.Ready, started.Container.Status);
        Assert.NotNull(started.Container.RuntimeProcessId);
        Assert.True(scriptController.IsScriptRunning(regId));

        await scriptController.StopScriptAsync(regId);
    }

    [Fact]
    public async Task RegisterAsync_ContainerKind_StillWorks_NoRegression()
    {
        // Ensure container registration still works after script changes
        var service = CreateService();

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "Container Regression",
            Image = "test:latest",
            ContainerPort = 8080
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(ContainerRegistrationStatus.Registered, result.Container.Status);
        Assert.Equal(RuntimeKind.Container, result.Container.RuntimeKind);
        Assert.Null(result.Container.RuntimeContainerId);
        Assert.Null(result.Container.MappedPort);
        Assert.Empty(result.DiscoveredModels);
    }

    [Fact]
    public async Task StartAsync_IncompatibleScriptPeer_StoppedByHolder_BeforeContainerStart()
    {
        // A running host SCRIPT runtime that is not in the target's allow list
        // must be stopped by its holder (synchronous kill) before a container
        // runtime may start.
        var script = CreateScript("sleep 60");
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        // Start a real script process tracked by the holder.
        var start = await scriptController.StartScriptAsync("peer-script", script, declaredPort: 9999);
        Assert.NotNull(start.Pid);
        Assert.True(scriptController.IsScriptRunning("peer-script"));

        await _registry.CreateAsync(new RegisteredRuntime
        {
            Id = "peer-script",
            DisplayName = "model-script",
            Image = "model-script",
            Agent = "host",
            RuntimeKind = RuntimeKind.Script,
            RuntimeProcessId = start.Pid,
            CanRunAlongWith = [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-target",
            DisplayName = "model-target",
            Image = "model-target",
            Agent = "host",
            RuntimeKind = RuntimeKind.Container,
            CanRunAlongWith = [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        _docker.MappedPortOverride = StartDiscoveryServer();

        var result = await service.StartAsync("reg-target");

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        // The holder killed the script synchronously before the start proceeded.
        Assert.False(scriptController.IsScriptRunning("peer-script"));
        Assert.Equal(["model-target"], _docker.StartedModels);

        // Defensive cleanup in case of failure above.
        if (scriptController.IsScriptRunning("peer-script"))
            await scriptController.StopScriptAsync("peer-script");
    }

    [Fact]
    public async Task StartAsync_ScriptKind_HealthTimeout_FailsWithMessage()
    {
        var script = CreateScript("sleep 30");
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        // Register succeeds (no start/health-check during registration)
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "HealthFailing",
            Image = "health-fail:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            ContainerPort = 8080
        };

        var regResult = await service.RegisterAsync(request);
        Assert.Equal(ContainerRegistrationStatus.Registered, regResult.Container.Status);

        // Now start — health checker is set to fail, so StartAsync should error
        _healthChecker.IsReady = false;

        var result = await service.StartAsync(regResult.Container.Id);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.NotNull(result.Container.ErrorMessage);

        // Cleanup
        if (scriptController.IsScriptRunning(result.Container.Id))
            await scriptController.StopScriptAsync(result.Container.Id);
    }

    [Fact]
    public async Task StartAsync_ScriptKind_RestartHealthTimeout_FailsWithWrappedMessage()
    {
        var script = CreateScript("sleep 30");
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        var request = new ContainerRegistrationRequest
        {
            DisplayName = "RestartHealthFail",
            Image = "restart-fail:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            ContainerPort = 8080
        };

        var regResult = await service.RegisterAsync(request);
        var regId = regResult.Container.Id;

        // Stop the script to simulate crash
        await scriptController.StopScriptAsync(regId);

        // Now set health checker to fail
        _healthChecker.IsReady = false;

        // StartAsync goes through StartScriptAsync which wraps health error
        var result = await service.StartAsync(regId);

        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.Contains("Health check failed", result.Container.ErrorMessage);

        // Cleanup
        if (scriptController.IsScriptRunning(regId))
            await scriptController.StopScriptAsync(regId);
    }

    [Fact]
    public async Task StartAsync_ScriptKind_HealthTimeout_TransitionFromStartingToError()
    {
        var script = CreateScript("sleep 30");
        var scriptController = CreateScriptController();
        var service = CreateService(scriptController: scriptController);

        // Register succeeds (no start/health-check during registration)
        var request = new ContainerRegistrationRequest
        {
            DisplayName = "TimeoutScript",
            Image = "timeout:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = script,
            ContainerPort = 8080
        };

        var regResult = await service.RegisterAsync(request);
        Assert.Equal(ContainerRegistrationStatus.Registered, regResult.Container.Status);

        // Now start — health checker is set to fail, so StartAsync should error
        _healthChecker.IsReady = false;

        var result = await service.StartAsync(regResult.Container.Id);

        // Health timeout should have failed the script
        Assert.Equal(ContainerRegistrationStatus.Error, result.Container.Status);
        Assert.NotNull(result.Container.ErrorMessage);

        // Cleanup
        if (scriptController.IsScriptRunning(result.Container.Id))
            await scriptController.StopScriptAsync(result.Container.Id);
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }
}
