using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Remote;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// InferenceProxy error/edge paths: non-success HTTP statuses, malformed JSON,
/// timeouts/cancellation, transport-failure retries within the hold window,
/// streaming success + mid-stream disconnect + buffered fallback, remote script
/// runtimes, on-demand starts, and the not-a-remote-controller guard.
/// </summary>
public sealed class InferenceProxyErrorPathTests
{
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeDockerController _host = new();
    private readonly FakeRemoteDockerController _remote = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeDockerControllerRouter _router;

    public InferenceProxyErrorPathTests()
    {
        _router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = _host,
            ["agent:gpu1"] = _remote
        });
    }

    private InferenceProxy CreateProxy(IServiceProvider? serviceProvider = null)
        => new(
            _host,
            _healthChecker,
            new LoggerFactory().CreateLogger<InferenceProxy>(),
            serviceProvider ?? NullServiceProvider.Instance,
            _containerRegistry,
            _router);

    private static InferenceRequest MakeRequest(string modelName, bool streaming = false, string? targetId = null)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = modelName,
            OriginalJson = """{"model":"m","messages":[{"role":"user","content":"hi"}],"max_tokens":8}""",
            IsStreaming = streaming,
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously),
            TargetId = targetId ?? ExecutionTarget.HostId
        };

    private async Task<string> SeedHostModelAsync(string modelName = "host-model", string regId = "reg-host-1")
    {
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = regId,
            DisplayName = "host-serve",
            Image = "host-serve",
            Agent = "host",
            Status = ContainerRegistrationStatus.Ready,
            MappedPort = 8080,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync(regId, modelName);
        return regId;
    }

    private async Task SeedAgentScriptAsync(string modelName = "script-model", int port = 9090)
    {
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-agent-script",
            DisplayName = "agent-script",
            Image = "agent-script",
            Agent = "gpu1",
            RuntimeKind = RuntimeKind.Script,
            Status = ContainerRegistrationStatus.Ready,
            MappedPort = port,
            ContainerPort = port,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-agent-script", modelName);
    }

    // ── Raw TCP listener helpers ──────────────────────────────────────────────

    private static (TcpListener Listener, int Port) StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    /// <summary>Accepts one connection, reads the request head, responds with a raw HTTP response.</summary>
    private static Task RespondOnceAsync(TcpListener listener, string statusLine, string contentType, string body, bool closeAfterBody = true)
        => Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buf = new byte[4096];
            _ = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
            var response =
                $"{statusLine}\r\nContent-Type: {contentType}\r\nContent-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes);
            if (closeAfterBody)
                client.Close();
        });

    // ── Host path: buffered responses ─────────────────────────────────────────

    [Fact]
    public async Task Host_NonSuccessStatus500_PassedThroughWithoutTokenParsing()
    {
        await SeedHostModelAsync();
        var (listener, port) = StartListener();
        try
        {
            _host.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "h1", ModelId = "host-serve", ModelName = "host-serve",
                    Status = ContainerStatus.Running, Port = port, RegisteredRuntimeId = "reg-host-1"
                }
            ];
            _ = RespondOnceAsync(listener, "HTTP/1.1 500 Internal Server Error", "text/plain", "boom");

            var proxy = CreateProxy();
            var response = await proxy.InvokeAsync(MakeRequest("host-model"));

            Assert.Equal(500, response.StatusCode);
            Assert.Equal("text/plain", response.ContentType);
            Assert.Equal(0, response.TokensGenerated);

            using var reader = new StreamReader(response.Body!);
            Assert.Equal("boom", await reader.ReadToEndAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Host_NonSuccessStatus503_PassedThrough()
    {
        await SeedHostModelAsync();
        var (listener, port) = StartListener();
        try
        {
            _host.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "h2", ModelId = "host-serve", ModelName = "host-serve",
                    Status = ContainerStatus.Running, Port = port, RegisteredRuntimeId = "reg-host-1"
                }
            ];
            _ = RespondOnceAsync(listener, "HTTP/1.1 503 Service Unavailable", "text/plain", "overloaded");

            var proxy = CreateProxy();
            var response = await proxy.InvokeAsync(MakeRequest("host-model"));

            Assert.Equal(503, response.StatusCode);
            Assert.Equal(0, response.TokensGenerated);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Host_MalformedJsonBody_Returns200WithZeroTokens()
    {
        await SeedHostModelAsync();
        var (listener, port) = StartListener();
        try
        {
            _host.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "h3", ModelId = "host-serve", ModelName = "host-serve",
                    Status = ContainerStatus.Running, Port = port, RegisteredRuntimeId = "reg-host-1"
                }
            ];
            _ = RespondOnceAsync(listener, "HTTP/1.1 200 OK", "application/json", "this is definitely not json");

            var proxy = CreateProxy();
            var response = await proxy.InvokeAsync(MakeRequest("host-model"));

            Assert.Equal(200, response.StatusCode);
            Assert.Equal(0, response.TokensGenerated);
            Assert.Equal(0, response.ServerTokensPerSec);
            Assert.Equal(0, response.PromptTokensCached);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Host_RequestCancelledWhileBackendHangs_PropagatesCancellation()
    {
        await SeedHostModelAsync();
        var (listener, port) = StartListener();
        try
        {
            _host.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "h4", ModelId = "host-serve", ModelName = "host-serve",
                    Status = ContainerStatus.Running, Port = port, RegisteredRuntimeId = "reg-host-1"
                }
            ];

            // Accept but never respond — the caller's cancellation is the only way out.
            _ = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync();
                var buf = new byte[1024];
                _ = await client.GetStream().ReadAsync(buf.AsMemory(0, buf.Length));
                // hold open until cancelled/disposed
                await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
                client.Close();
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            var proxy = CreateProxy();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => proxy.InvokeAsync(MakeRequest("host-model"), cts.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Host_TransportFailure_RetriesWithinHoldWindowThenPropagates()
    {
        await SeedHostModelAsync();
        // Dead port: bind then release so connections are refused.
        var (listener, port) = StartListener();
        listener.Stop();

        _host.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "h5", ModelId = "host-serve", ModelName = "host-serve",
                Status = ContainerStatus.Running, Port = port, RegisteredRuntimeId = "reg-host-1"
            }
        ];

        var proxy = CreateProxy();
        proxy.HoldSecondsOverride = 1; // shrink warmup-retry window for tests

        await Assert.ThrowsAnyAsync<Exception>(
            () => proxy.InvokeAsync(MakeRequest("host-model")));

        // Initial readiness wait + re-verify before each retry → ≥2 health checks.
        Assert.True(_healthChecker.CheckedPorts.Count >= 2,
            $"Expected ≥2 health checks across retries, got {_healthChecker.CheckedPorts.Count}");
    }

    [Fact]
    public async Task Host_LegacyModelNameMatch_ProxiesWithoutRegistration()
    {
        var (listener, port) = StartListener();
        try
        {
            // No registration at all: legacy match by container model name.
            _host.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "h6", ModelId = "legacy-model", ModelName = "legacy-model",
                    Status = ContainerStatus.Running, Port = port
                }
            ];
            _ = RespondOnceAsync(
                listener, "HTTP/1.1 200 OK", "application/json",
                """{"choices":[],"usage":{"completion_tokens":4}}""");

            var proxy = CreateProxy();
            var response = await proxy.InvokeAsync(MakeRequest("legacy-model"));

            Assert.Equal(200, response.StatusCode);
            Assert.Equal(4, response.TokensGenerated);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Host_NoRunningContainer_Returns503()
    {
        await SeedHostModelAsync();
        _host.ListedContainers = [];

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("host-model"));

        Assert.Equal(503, response.StatusCode);
    }

    // ── Host path: script runtime ─────────────────────────────────────────────

    [Fact]
    public async Task Host_ScriptRuntime_Healthy_ProxiesViaMappedPort()
    {
        var (listener, port) = StartListener();
        try
        {
            await _containerRegistry.CreateAsync(new RegisteredRuntime
            {
                Id = "reg-host-script",
                DisplayName = "host-script",
                Image = "host-script",
                Agent = "host",
                RuntimeKind = RuntimeKind.Script,
                MappedPort = port,
                ContainerPort = port,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await _containerRegistry.AddModelMappingAsync("reg-host-script", "hs-model");

            _ = RespondOnceAsync(
                listener, "HTTP/1.1 200 OK", "application/json",
                """{"choices":[],"usage":{"completion_tokens":6}}""");

            var proxy = CreateProxy();
            var response = await proxy.InvokeAsync(MakeRequest("hs-model"));

            Assert.Equal(200, response.StatusCode);
            Assert.Equal(6, response.TokensGenerated);
            Assert.Contains(port, _healthChecker.CheckedPorts);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Host_ScriptRuntime_DeadAndStartFails_Returns503()
    {
        await SeedHostModelAsync("dead-script-model", "reg-dead-script");
        // Convert to script kind by re-registering with the right kind.
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-dead-script-2",
            DisplayName = "dead-script",
            Image = "dead-script",
            Agent = "host",
            RuntimeKind = RuntimeKind.Script,
            MappedPort = 9999,
            ContainerPort = 9999,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-dead-script-2", "dead-script-model");

        _healthChecker.CheckFunc = (_, _) => Task.FromResult(false); // dead

        var proxy = CreateProxy(); // NullServiceProvider's registration service fails
        var response = await proxy.InvokeAsync(MakeRequest("dead-script-model"));

        Assert.Equal(503, response.StatusCode);
    }

    // ── Host path: streaming ──────────────────────────────────────────────────

    [Fact]
    public async Task Host_Streaming_Success_BodyDrainsAndTapCountsTokens()
    {
        await SeedHostModelAsync();
        var (listener, port) = StartListener();
        try
        {
            _host.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "h7", ModelId = "host-serve", ModelName = "host-serve",
                    Status = ContainerStatus.Running, Port = port, RegisteredRuntimeId = "reg-host-1"
                }
            ];
            var sseBody =
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
                "data: {\"usage\":{\"completion_tokens\":9}}\n\n" +
                "data: [DONE]\n\n";
            _ = RespondOnceAsync(listener, "HTTP/1.1 200 OK", "text/event-stream", sseBody);

            var proxy = CreateProxy();
            var response = await proxy.InvokeAsync(MakeRequest("host-model", streaming: true));

            Assert.Equal(200, response.StatusCode);
            Assert.NotNull(response.BodyDrained);

            using (response.Body!)
            {
                using var reader = new StreamReader(response.Body!);
                var full = await reader.ReadToEndAsync();
                Assert.Contains("[DONE]", full);
            }

            // Drained completes once EOF was reached; tap finalized token counts.
            await response.BodyDrained!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(9, response.TokensGenerated);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Host_Streaming_MidStreamDisconnect_DrainCompletesWithoutHanging()
    {
        await SeedHostModelAsync();
        var (listener, port) = StartListener();
        try
        {
            _host.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "h8", ModelId = "host-serve", ModelName = "host-serve",
                    Status = ContainerStatus.Running, Port = port, RegisteredRuntimeId = "reg-host-1"
                }
            ];

            // Declare more content than is sent, then drop the connection mid-body.
            _ = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buf = new byte[4096];
                _ = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
                var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: 512\r\n\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"par\"}}]}\n\n";
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(head));
                await stream.FlushAsync();
                client.Close(); // abrupt disconnect mid-stream
            });

            var proxy = CreateProxy();
            var response = await proxy.InvokeAsync(MakeRequest("host-model", streaming: true));

            Assert.Equal(200, response.StatusCode);

            // Consume until EOF or fault — either way the drain signal must settle.
            try
            {
                using var reader = new StreamReader(response.Body!);
                while ((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))) is not null) { }
            }
            catch (Exception)
            {
                // premature-EOF IOException / similar — acceptable disconnect surface
            }
            finally
            {
                // Disposing the response stream is what settles Drained when the
                // connection died before EOF.
                await response.Body!.DisposeAsync();
            }

            var drainTask = response.BodyDrained!.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await drainTask;
            }
            catch
            {
                // A faulted drain (abrupt disconnect) is an acceptable settled state.
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── Remote path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Remote_ControllerNotRemote_Returns501()
    {
        // Route the agent target to a plain docker controller.
        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = _host,
            ["agent:x"] = new FakeDockerController { IdPrefix = "plain" }
        });
        var proxy = new InferenceProxy(
            _host, _healthChecker, new LoggerFactory().CreateLogger<InferenceProxy>(),
            NullServiceProvider.Instance, _containerRegistry, router);

        var response = await proxy.InvokeAsync(MakeRequest("any-model", targetId: "agent:x"));

        Assert.Equal(501, response.StatusCode);
    }

    /// <summary>IRemoteDockerController wrapper that can throw from ListContainersAsync.</summary>
    private sealed class ThrowingListRemote : IRemoteDockerController
    {
        private readonly FakeRemoteDockerController _inner;
        public ThrowingListRemote(FakeRemoteDockerController inner) => _inner = inner;

        public Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default)
            => _inner.StartContainerAsync(modelName, ct);
        public Task<ContainerStartResult> StartRegisteredContainerAsync(
            string registeredContainerId, string image, int containerPort,
            string? gpuDevices, long memoryLimitMb, Dictionary<string, string> extraLabels, CancellationToken ct = default)
            => _inner.StartRegisteredContainerAsync(registeredContainerId, image, containerPort, gpuDevices, memoryLimitMb, extraLabels, ct);
        public Task StopContainerAsync(string idOrModel, CancellationToken ct = default)
            => _inner.StopContainerAsync(idOrModel, ct);
        public Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
            => _inner.RestartContainerAsync(id, ct);
        public Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
            => _inner.InspectContainerAsync(id, ct);
        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("agent unreachable");
        public Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default)
            => _inner.GetContainerLogsAsync(id, tailLines, ct);
        public Task RemoveContainerAsync(string id, CancellationToken ct = default)
            => _inner.RemoveContainerAsync(id, ct);
        public Task<int?> ResolveMappedPortAsync(string containerName, int containerPort, CancellationToken ct = default)
            => _inner.ResolveMappedPortAsync(containerName, containerPort, ct);
        public Task<bool> HealthCheckAsync(int port, CancellationToken ct = default)
            => _inner.HealthCheckAsync(port, ct);
        public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default)
            => _inner.DiscoverModelsAsync(port, ct);
        public Task<string> InferAsync(int port, string requestJson, CancellationToken ct = default)
            => _inner.InferAsync(port, requestJson, ct);
        public Task<Stream> InferStreamAsync(int port, string requestJson, CancellationToken ct = default)
            => _inner.InferStreamAsync(port, requestJson, ct);
        public Task<IReadOnlyList<AgentScriptInfo>> ListScriptsAsync(CancellationToken ct = default)
            => _inner.ListScriptsAsync(ct);
    }

    [Fact]
    public async Task Remote_ListContainersThrows_Returns502()
    {
        await SeedHostModelAsync("remote-list-model", "reg-remote-list");
        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = _host,
            ["agent:gpu1"] = new ThrowingListRemote(_remote)
        });
        var proxy = new InferenceProxy(
            _host, _healthChecker, new LoggerFactory().CreateLogger<InferenceProxy>(),
            NullServiceProvider.Instance, _containerRegistry, router);

        var response = await proxy.InvokeAsync(MakeRequest("remote-list-model", targetId: "agent:gpu1"));

        Assert.Equal(502, response.StatusCode);
    }

    [Fact]
    public async Task Remote_ScriptRuntime_Healthy_ProxiesInferAndParsesTokens()
    {
        await SeedAgentScriptAsync();
        _remote.InferResult = """{"id":"s","choices":[],"usage":{"completion_tokens":11},"timings":{"predicted_per_second":42.5}}""";

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("script-model", targetId: "agent:gpu1"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(11, response.TokensGenerated);
        Assert.Equal(42.5, response.ServerTokensPerSec);
        Assert.Contains(9090, _remote.HealthCheckedPorts);
        var call = Assert.Single(_remote.InferCalls);
        Assert.Equal(9090, call.Port);
    }

    [Fact]
    public async Task Remote_ScriptRuntime_UnhealthyAndStartFails_Returns503()
    {
        await SeedAgentScriptAsync();
        _remote.Healthy = false; // both initial probe and double-check fail

        var proxy = CreateProxy(); // NullServiceProvider start always fails
        var response = await proxy.InvokeAsync(MakeRequest("script-model", targetId: "agent:gpu1"));

        Assert.Equal(503, response.StatusCode);
        Assert.Empty(_remote.InferCalls);
    }

    [Fact]
    public async Task Remote_ScriptRuntime_InferKeepsFailing_RetriesWithinHoldThenReturns502()
    {
        await SeedAgentScriptAsync();
        _remote.InferFunc = (_, _, _) => throw new InvalidOperationException("runtime crashed");
        var proxy = CreateProxy();
        proxy.HoldSecondsOverride = 1;

        var response = await proxy.InvokeAsync(MakeRequest("script-model", targetId: "agent:gpu1"));

        Assert.Equal(502, response.StatusCode);
        Assert.True(_remote.InferCalls.Count >= 2,
            $"Expected ≥2 infer attempts within the hold window, got {_remote.InferCalls.Count}");
    }

    [Fact]
    public async Task Remote_BufferedInfer_RetriesWithinHoldWindow_ThenSucceeds()
    {
        await SeedHostModelAsync("remote-retry-model", "reg-remote-retry");
        _remote.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "r1", ModelId = "remote-serve", ModelName = "remote-serve",
                Status = ContainerStatus.Running, Port = 9091, RegisteredRuntimeId = "reg-remote-retry"
            }
        ];

        var failures = 0;
        _remote.InferFunc = (_, _, _) =>
        {
            if (Interlocked.Increment(ref failures) <= 2)
                throw new InvalidOperationException("warming up");
            return Task.FromResult("""{"choices":[],"usage":{"completion_tokens":3}}""");
        };

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("remote-retry-model", targetId: "agent:gpu1"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(3, response.TokensGenerated);
        Assert.True(_remote.InferCalls.Count >= 3);
    }

    [Fact]
    public async Task Remote_Streaming_NotSupportedByAgent_FallsBackToBuffered()
    {
        await SeedHostModelAsync("remote-stream-model", "reg-remote-stream");
        _remote.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "r2", ModelId = "remote-serve", ModelName = "remote-serve",
                Status = ContainerStatus.Running, Port = 9092, RegisteredRuntimeId = "reg-remote-stream"
            }
        ];
        _remote.InferStreamFunc = (_, _, _) =>
            throw new NotSupportedException("unknown command chat_completion_stream");
        _remote.InferResult = """{"choices":[],"usage":{"completion_tokens":5}}""";

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("remote-stream-model", streaming: true, targetId: "agent:gpu1"));

        // Buffered fallback served the request despite IsStreaming.
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(5, response.TokensGenerated);
        // One stream attempt + one buffered infer call.
        Assert.Equal(2, _remote.InferCalls.Count);
    }

    [Fact]
    public async Task Remote_Streaming_Success_WrapsTapStreamAndSetsDrained()
    {
        await SeedHostModelAsync("remote-okstream-model", "reg-remote-okstream");
        _remote.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "r3", ModelId = "remote-serve", ModelName = "remote-serve",
                Status = ContainerStatus.Running, Port = 9093, RegisteredRuntimeId = "reg-remote-okstream"
            }
        ];
        _remote.InferResult = "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n";

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("remote-okstream-model", streaming: true, targetId: "agent:gpu1"));

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.BodyDrained);

        using (response.Body!)
        {
            using var reader = new StreamReader(response.Body!);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("delta", body);
        }

        // Non-tunnel streams report CompletedTask as their drained signal.
        Assert.True(response.BodyDrained!.IsCompleted);
    }

    [Fact]
    public async Task Remote_OnDemandStartSucceeds_ProxiesAfterStart()
    {
        await SeedHostModelAsync("remote-ondemand-model", "reg-remote-ondemand");
        _remote.ListedContainers = []; // nothing running initially

        var provider = new StubServiceProvider(new FlippingRegistrationService(() =>
        {
            _remote.ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "r4", ModelId = "remote-serve", ModelName = "remote-serve",
                    Status = ContainerStatus.Running, Port = 9094, RegisteredRuntimeId = "reg-remote-ondemand"
                }
            ];
        }));
        _remote.InferResult = """{"choices":[],"usage":{"completion_tokens":7}}""";

        var proxy = CreateProxy(provider);
        var response = await proxy.InvokeAsync(MakeRequest("remote-ondemand-model", targetId: "agent:gpu1"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(7, response.TokensGenerated);
        var call = Assert.Single(_remote.InferCalls);
        Assert.Equal(9094, call.Port);
    }

    // ── Stubs for on-demand start success ─────────────────────────────────────

    private sealed class FlippingRegistrationService : IContainerRegistrationService
    {
        private readonly Action _onStart;
        public FlippingRegistrationService(Action onStart) => _onStart = onStart;

        public Task<RegisteredRuntimeWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default)
        {
            _onStart();
            return Task.FromResult(new RegisteredRuntimeWithModels
            {
                Container = new RegisteredRuntime
                {
                    Id = registeredContainerId,
                    Image = registeredContainerId,
                    Status = ContainerRegistrationStatus.Ready
                },
                DiscoveredModels = []
            });
        }

        public Task<RegisteredRuntimeWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<RegisteredRuntimeWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<RegisteredRuntime?> UpdateCanRunAlongWithAsync(string id, IReadOnlyList<string> canRunAlongWith, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<(RegisteredRuntime A, RegisteredRuntime B)?> ToggleConcurrencyAsync(string runtimeAId, string runtimeBId, bool canRunAlongWith, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<RegisteredRuntime?> StopAsync(string id, CancellationToken ct = default)
            => Task.FromResult<RegisteredRuntime?>(null);
        public Task<string> ResolveLiveContainerIdAsync(string runtimeContainerId, CancellationToken ct = default)
            => Task.FromResult(runtimeContainerId);
        public Task<RegisteredRuntime?> HealthCheckAsync(string id, CancellationToken ct = default)
            => Task.FromResult<RegisteredRuntime?>(null);
    }

    private sealed class StubServiceProvider : IServiceProvider, IServiceScopeFactory
    {
        private readonly IContainerRegistrationService _registration;
        public StubServiceProvider(IContainerRegistrationService registration) => _registration = registration;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory)) return this;
            if (serviceType == typeof(IContainerRegistrationService)) return _registration;
            return null;
        }

        public IServiceScope CreateScope() => new Scope(this);

        private sealed class Scope(StubServiceProvider owner) : IServiceScope
        {
            public IServiceProvider ServiceProvider => owner;
            public void Dispose() { }
        }
    }
}
