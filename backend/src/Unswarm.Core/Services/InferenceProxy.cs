using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Core.Services;

public sealed class InferenceProxy : IInferenceProxy
{
    private readonly IDockerController _docker;
    private readonly IDockerControllerRouter _router;
    private readonly IHealthChecker _healthChecker;
    private readonly IContainerRegistry? _containerRegistry;
    private readonly ILogger<InferenceProxy> _logger;
    private readonly ILogStore? _logStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new(StringComparer.Ordinal);
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    // Readiness + hold budgets: a request holds the connection while its target
    // runtime starts/warms up instead of surfacing transient states as failures.
    private const int ReadyTimeoutSeconds = 120;
    private const int ProxyHoldSeconds = 180;
    private const int RetryDelayMs = 500;

    public InferenceProxy(
        IDockerController docker,
        IHealthChecker healthChecker,
        ILogger<InferenceProxy> logger,
        IServiceProvider serviceProvider,
        IContainerRegistry? containerRegistry = null,
        IDockerControllerRouter? router = null,
        ILogStore? logStore = null)
    {
        _docker = docker;
        _healthChecker = healthChecker;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _containerRegistry = containerRegistry;
        _router = router ?? new HostOnlyDockerControllerRouter(docker);
        _logStore = logStore;
    }

    public async Task<InferenceResponse> InvokeAsync(InferenceRequest request, CancellationToken ct = default)
    {
        var targetId = request.TargetId ?? ExecutionTarget.HostId;
        var controller = _router.GetController(targetId);

        if (targetId != ExecutionTarget.HostId)
        {
            return await InvokeRemoteAsync(request, targetId, controller, ct).ConfigureAwait(false);
        }

        // Host path: find the running container that serves this model.
        // 1. Registered-container lookup (via the unswarm.registry label populated by
        //    DockerController.ListContainersAsync) — most precise.
        // 2. Fallback: model name/image match against the registered container's
        //    Image/DisplayName (mirrors remote resolution semantics).
        // 3. Legacy: standalone model-name label path.
        RegisteredRuntime? registered = null;
        string? registeredContainerId = null;
        if (_containerRegistry is not null)
        {
            registeredContainerId = await _containerRegistry
                .GetContainerIdForModelAsync(request.ModelName, ct).ConfigureAwait(false);
            if (registeredContainerId is not null)
            {
                registered = await _containerRegistry.GetAsync(registeredContainerId, ct).ConfigureAwait(false);
            }
        }

        // Script runtimes: use mapped port directly (they don't appear in docker ps).
        // A dead script is restarted through its owner instead of failing the caller —
        // the connection is held until the runtime is ready and serving.
        if (registered is not null && registered.RuntimeKind == RuntimeKind.Script)
        {
            var scriptPort = registered.MappedPort ?? registered.ContainerPort;
            if (!await _healthChecker.CheckAsync(scriptPort, ct).ConfigureAwait(false))
            {
                var startLock = _startLocks.GetOrAdd(registeredContainerId!, _ => new SemaphoreSlim(1, 1));
                await startLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Double-check: another request may have restarted it while we waited.
                    if (!await _healthChecker.CheckAsync(scriptPort, ct).ConfigureAwait(false))
                    {
                        var startError = await StartOnDemandAsync(registeredContainerId!, request.ModelName, ct).ConfigureAwait(false);
                        if (startError is not null) return startError;
                    }
                }
                finally
                {
                    startLock.Release();
                }
            }
            return await WaitReadyAndProxyAsync(request, scriptPort, ct).ConfigureAwait(false);
        }

        var containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
        var running = containers.Where(c => c.Status == ContainerStatus.Running && c.Port.HasValue).ToList();

        ContainerInfo? container = null;
        if (!string.IsNullOrEmpty(registeredContainerId))
        {
            // Match by registry label first.
            container = running.FirstOrDefault(c => c.RegisteredRuntimeId == registeredContainerId);

            // Fallback: match by runtime container id or by the registered container's
            // image/display name (the container name on docker ps).
            if (container is null && registered is not null)
            {
                var names = RegisteredContainerNames(registered);
                container = running.FirstOrDefault(c =>
                    names.Contains(c.ModelName) || names.Contains(c.ModelId));
            }
        }

        // Legacy path for standalone models (no registration): match by model name.
        container ??= running.FirstOrDefault(c => c.ModelName == request.ModelName);

        // If no running container found but a registered container exists, start it on demand
        if (container?.Port is not { } port && registeredContainerId is not null && registered is not null)
        {
            var startLock = _startLocks.GetOrAdd(registeredContainerId, _ => new SemaphoreSlim(1, 1));
            await startLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check: another request may have started it while we waited
                containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
                running = containers.Where(c => c.Status == ContainerStatus.Running && c.Port.HasValue).ToList();
                container = running.FirstOrDefault(c => c.RegisteredRuntimeId == registeredContainerId);

                if (container?.Port is null)
                {
                    // Coexistence + start are owned by ContainerRegistrationService.StartAsync:
                    // it stops every runtime on this agent that is not in the target's allow
                    // list, confirms each one stopped, then starts the requested runtime.
                    // The registry Status is deliberately NOT consulted here — a stale Ready
                    // must not skip an actually-not-running container.
                    _logger.LogInformation(
                        "On-demand starting container {Id} for model {Model}",
                        registeredContainerId[..Math.Min(12, registeredContainerId.Length)], request.ModelName);

                    await using var scope = _serviceProvider.CreateAsyncScope();
                    var registrationService = scope.ServiceProvider.GetRequiredService<IContainerRegistrationService>();
                    var result = await registrationService.StartAsync(registeredContainerId, ct).ConfigureAwait(false);

                    if (result.Container.Status != ContainerRegistrationStatus.Ready &&
                        result.Container.Status != ContainerRegistrationStatus.Healthy)
                    {
                        _logger.LogWarning(
                            "Failed to start container {Id} for model {Model}: {Error}",
                            registeredContainerId[..Math.Min(12, registeredContainerId.Length)],
                            request.ModelName,
                            result.Container.ErrorMessage ?? "unknown error");
                        return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
                    }

                    // Re-resolve: the container should now be running
                    containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
                    running = containers.Where(c => c.Status == ContainerStatus.Running && c.Port.HasValue).ToList();
                    container = running.FirstOrDefault(c => c.RegisteredRuntimeId == registeredContainerId);
                }
            }
            finally
            {
                startLock.Release();
            }
        }

        if (container?.Port is not { } resolvedPort)
        {
            _logger.LogWarning("No running container found for model {Model}", request.ModelName);
            return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
        }

        return await WaitReadyAndProxyAsync(request, resolvedPort, ct).ConfigureAwait(false);
    }

    private static IReadOnlySet<string> RegisteredContainerNames(RegisteredRuntime registered)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(registered.Image)) names.Add(registered.Image);
        if (!string.IsNullOrEmpty(registered.DisplayName)) names.Add(registered.DisplayName);
        return names;
    }

    private void LogProxy(LogLevel level, string message)
    {
        _logStore?.Enqueue(level, "proxy", message);
    }

    private async Task<InferenceResponse> InvokeRemoteAsync(
        InferenceRequest request,
        string targetId,
        IDockerController controller,
        CancellationToken ct)
    {
        // Remote inference is tunneled over the agent WebSocket via IRemoteDockerController.InferAsync.
        // Resolve the model's registered container and its mapped port on the target agent.
        if (controller is not IRemoteDockerController remote)
        {
            _logger.LogWarning(
                "Controller for target {Target} is not a remote controller; cannot proxy inference for model {Model}",
                targetId, request.ModelName);
            return new InferenceResponse { StatusCode = 501, ContentType = "text/plain" };
        }

        // Find the registered container serving this model on this agent.
        string? registeredContainerId = null;
        if (_containerRegistry is not null)
        {
            registeredContainerId = await _containerRegistry
                .GetContainerIdForModelAsync(request.ModelName, ct).ConfigureAwait(false);
        }

        // Script runtimes on agents don't appear in docker ps. If the registered runtime
        // is a script, use its port directly and skip the container listing entirely.
        // A dead agent script is restarted through its owner instead of failing the
        // caller — the connection is held until the runtime is ready and serving.
        if (registeredContainerId is not null && _containerRegistry is not null)
        {
            var registered = await _containerRegistry.GetAsync(registeredContainerId, ct).ConfigureAwait(false);
            if (registered is not null && registered.RuntimeKind == RuntimeKind.Script)
            {
                var scriptPort = registered.MappedPort ?? registered.ContainerPort;
                if (!await remote.HealthCheckAsync(scriptPort, ct).ConfigureAwait(false))
                {
                    var startLock = _startLocks.GetOrAdd(registeredContainerId, _ => new SemaphoreSlim(1, 1));
                    await startLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Double-check: another request may have restarted it while we waited.
                        if (!await remote.HealthCheckAsync(scriptPort, ct).ConfigureAwait(false))
                        {
                            var startError = await StartOnDemandAsync(registeredContainerId, request.ModelName, ct).ConfigureAwait(false);
                            if (startError is not null) return startError;
                        }
                    }
                    finally
                    {
                        startLock.Release();
                    }
                }

                // Bounded retry while the backend finishes warming up.
                var holdDeadline = DateTime.UtcNow.AddSeconds(ProxyHoldSeconds);
                while (true)
                {
                    string rawScriptBody;
                    try
                    {
                        rawScriptBody = await remote.InferAsync(scriptPort, request.OriginalJson, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && DateTime.UtcNow < holdDeadline)
                    {
                        _logger.LogWarning(ex,
                            "Remote script inference failed for model {Model} on target {Target} port {Port}; runtime may still be warming up — retrying within hold window",
                            request.ModelName, targetId, scriptPort);
                        await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Remote script inference failed for model {Model} on target {Target} port {Port}",
                            request.ModelName, targetId, scriptPort);
                        return new InferenceResponse { StatusCode = 502, ContentType = "text/plain" };
                    }

                    var scriptTokens = TryParseCompletionTokens(rawScriptBody);
                    var scriptServerTps = TryParseServerTokensPerSec(rawScriptBody);
                    return new InferenceResponse
                    {
                        StatusCode = 200,
                        ContentType = "application/json",
                        Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawScriptBody)),
                        TokensGenerated = scriptTokens,
                        ServerTokensPerSec = scriptServerTps
                    };
                }
            }
        }

        IReadOnlyList<ContainerInfo> containers;
        try
        {
            containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list containers on target {Target} for model {Model}", targetId, request.ModelName);
            return new InferenceResponse { StatusCode = 502, ContentType = "text/plain" };
        }

        ContainerInfo? container = null;
        if (!string.IsNullOrEmpty(registeredContainerId))
        {
            container = containers.FirstOrDefault(c =>
                c.RegisteredRuntimeId == registeredContainerId
                && c.Status == ContainerStatus.Running
                && c.Port.HasValue);
        }

        // Fallback: match by model/container name on the agent.
        container ??= containers.FirstOrDefault(c =>
            (c.ModelName == request.ModelName || c.ModelId == request.ModelName)
            && c.Status == ContainerStatus.Running
            && c.Port.HasValue);

        if (container?.Port is not { } port && registeredContainerId is not null)
        {
            // Try on-demand start for remote containers
            var startLock = _startLocks.GetOrAdd(registeredContainerId, _ => new SemaphoreSlim(1, 1));
            await startLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
                container = containers.FirstOrDefault(c =>
                    c.RegisteredRuntimeId == registeredContainerId
                    && c.Status == ContainerStatus.Running
                    && c.Port.HasValue);

                if (container?.Port is null)
                {
                    _logger.LogInformation(
                        "On-demand starting remote container {Id} for model {Model} on agent {Agent}",
                        registeredContainerId[..Math.Min(12, registeredContainerId.Length)], request.ModelName, targetId);

                    await using var scope = _serviceProvider.CreateAsyncScope();
                    var registrationService = scope.ServiceProvider.GetRequiredService<IContainerRegistrationService>();
                    var result = await registrationService.StartAsync(registeredContainerId, ct).ConfigureAwait(false);

                    if (result.Container.Status != ContainerRegistrationStatus.Ready &&
                        result.Container.Status != ContainerRegistrationStatus.Healthy)
                    {
                        _logger.LogWarning(
                            "Failed to start remote container {Id} for model {Model}: {Error}",
                            registeredContainerId[..Math.Min(12, registeredContainerId.Length)],
                            request.ModelName,
                            result.Container.ErrorMessage ?? "unknown error");
                        return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
                    }

                    containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
                    container = containers.FirstOrDefault(c =>
                        c.RegisteredRuntimeId == registeredContainerId
                        && c.Status == ContainerStatus.Running
                        && c.Port.HasValue);
                }
            }
            finally
            {
                startLock.Release();
            }
        }

        if (container?.Port is not { } resolvedPort)
        {
            _logger.LogWarning("No running container found for model {Model} on target {Target}", request.ModelName, targetId);
            return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
        }

        // Bounded retry while the backend finishes warming up.
        var containerHoldDeadline = DateTime.UtcNow.AddSeconds(ProxyHoldSeconds);
        while (true)
        {
            string rawBody;
            try
            {
                rawBody = await remote.InferAsync(resolvedPort, request.OriginalJson, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTime.UtcNow < containerHoldDeadline)
            {
                _logger.LogWarning(ex,
                    "Remote inference failed for model {Model} on target {Target} port {Port}; runtime may still be warming up — retrying within hold window",
                    request.ModelName, targetId, resolvedPort);
                await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote inference failed for model {Model} on target {Target} port {Port}",
                    request.ModelName, targetId, resolvedPort);
                return new InferenceResponse { StatusCode = 502, ContentType = "text/plain" };
            }

            var tokens = TryParseCompletionTokens(rawBody);
            var serverTps = TryParseServerTokensPerSec(rawBody);

            return new InferenceResponse
            {
                StatusCode = 200,
                ContentType = "application/json",
                Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawBody)),
                TokensGenerated = tokens,
                ServerTokensPerSec = serverTps
            };
        }
    }

    private static int TryParseCompletionTokens(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("completion_tokens", out var tokensProp)
                && tokensProp.ValueKind == System.Text.Json.JsonValueKind.Number
                && tokensProp.TryGetInt32(out var n))
            {
                return n;
            }
        }
        catch
        {
            // best-effort token parsing; 0 is fine
        }
        return 0;
    }

    /// <summary>
    /// Best-effort extraction of the server-reported token generation throughput.
    /// Returns the predicted tokens/sec when the server exposes timing data,
    /// otherwise 0. Checks:
    ///   1. llama.cpp OpenAI-compatible: root <c>timings</c> → <c>predicted_per_second</c>
    ///   2. Ollama-style: root <c>eval_count</c> / <c>eval_duration</c> (ns)
    /// </summary>
    private static double TryParseServerTokensPerSec(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 1. llama.cpp OpenAI-compatible: timings.predicted_per_second
            if (root.TryGetProperty("timings", out var timings)
                && timings.TryGetProperty("predicted_per_second", out var pps)
                && pps.ValueKind == System.Text.Json.JsonValueKind.Number
                && pps.TryGetDouble(out var rate)
                && rate > 0)
            {
                return rate;
            }

            // 2. Ollama-style: eval_count / (eval_duration_ns / 1e9)
            if (root.TryGetProperty("eval_count", out var evalCount)
                && evalCount.ValueKind == System.Text.Json.JsonValueKind.Number
                && root.TryGetProperty("eval_duration", out var evalDur)
                && evalDur.ValueKind == System.Text.Json.JsonValueKind.Number
                && evalDur.TryGetDouble(out var durNs)
                && durNs > 0)
            {
                if (evalCount.TryGetDouble(out var count) && count > 0)
                {
                    return count / (durNs / 1_000_000_000.0);
                }
            }
        }
        catch
        {
            // best-effort; 0 means fall back to stopwatch-derived value
        }
        return 0;
    }

    /// <summary>
    /// Stream wrapper that disposes the <see cref="HttpResponseMessage"/> when the
    /// underlying stream is closed/disposed, keeping the response alive only for the
    /// duration of the pipe. This enables true real-time streaming: chunks arrive
    /// on the client as soon as they are produced by the backend, without buffering
    /// the full response in memory.
    ///
    /// Exposes <see cref="Drained"/>: a Task that completes when the inner stream
    /// reaches EOF (ReadAsync returns 0), the stream is disposed, or a fault occurs.
    /// The scheduler awaits this before releasing the per-target worker slot.
    /// </summary>
    private sealed class HttpResponseMessageStream : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _inner;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public HttpResponseMessageStream(HttpResponseMessage response, Stream inner)
        {
            _response = response;
            _inner = inner;
        }

        /// <summary>
        /// Completes when the inner stream has been fully consumed (EOF), disposed,
        /// or faulted. The scheduler awaits this to prevent premature model switching.
        /// </summary>
        public Task Drained => _drained.Task;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => await _inner.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var n = await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
            if (n == 0)
                _drained.TrySetResult();
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
                _drained.TrySetResult();
            return n;
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _drained.TrySetException(ex);
                _response.Dispose();
                return;
            }
            _response.Dispose();
            _drained.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try
                {
                    _inner.Dispose();
                }
                catch (Exception ex)
                {
                    _drained.TrySetException(ex);
                    _response.Dispose();
                    return;
                }
                _response.Dispose();
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Starts (or restarts) a registered runtime through ContainerRegistrationService.
    /// The service owns coexistence enforcement, the real start, and the readiness
    /// wait. Returns null on success, or a 503 response when the start failed.
    /// Callers hold their own per-runtime start lock and double-check liveness first.
    /// </summary>
    private async Task<InferenceResponse?> StartOnDemandAsync(string registeredContainerId, string modelName, CancellationToken ct)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var registrationService = scope.ServiceProvider.GetRequiredService<IContainerRegistrationService>();

        _logger.LogInformation(
            "On-demand starting runtime {Id} for model {Model}",
            registeredContainerId[..Math.Min(12, registeredContainerId.Length)], modelName);

        var result = await registrationService.StartAsync(registeredContainerId, ct).ConfigureAwait(false);
        if (result.Container.Status != ContainerRegistrationStatus.Ready &&
            result.Container.Status != ContainerRegistrationStatus.Healthy)
        {
            _logger.LogWarning(
                "Failed to start runtime {Id} for model {Model}: {Error}",
                registeredContainerId[..Math.Min(12, registeredContainerId.Length)],
                modelName,
                result.Container.ErrorMessage ?? "unknown error");
            return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
        }

        return null;
    }

    /// <summary>
    /// Holds the connection until the runtime on <paramref name="port"/> is ready
    /// and actually serving: waits for health, then proxies with bounded retries
    /// on transport-level failures (a backend that answered HTTP is returned
    /// as-is). Only timeout or cancellation surfaces as failure.
    /// </summary>
    private async Task<InferenceResponse> WaitReadyAndProxyAsync(InferenceRequest request, int port, CancellationToken ct)
    {
        await _healthChecker.WaitForReadyAsync(port, ReadyTimeoutSeconds, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(ProxyHoldSeconds);
        while (true)
        {
            try
            {
                return await ProxyToPortCoreAsync(request, port, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or IOException or SocketException
                && ex is not OperationCanceledException
                && DateTime.UtcNow < deadline)
            {
                _logger.LogWarning(ex,
                    "Inference transport failure for model {Model} on port {Port}; runtime may still be warming up — retrying within hold window",
                    request.ModelName, port);
                LogProxy(LogLevel.Warn,
                    $"Transport error for model {request.ModelName} on port {port}: {ex.Message}; retrying");
                await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                // Re-verify readiness before the next attempt so a runtime that
                // bounced (restart/loading) is given time to come back.
                await _healthChecker.WaitForReadyAsync(port, ReadyTimeoutSeconds, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<InferenceResponse> ProxyToPortCoreAsync(InferenceRequest request, int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}/v1/chat/completions";

        using var httpContent = new StringContent(
            request.OriginalJson,
            System.Text.Encoding.UTF8,
            "application/json");

        var isStreaming = request.IsStreaming;

        // For streaming: use SendAsync with ResponseHeadersRead so the content
        // stream stays open for real-time piping. The response is disposed
        // automatically by HttpResponseMessageStream once the pipe is drained.
        if (isStreaming)
        {
            var sendRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = httpContent
            };
            var response = await SharedHttp.SendAsync(
                sendRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct)
                .ConfigureAwait(false);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            var statusCode = (int)response.StatusCode;

            var innerStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // Wrap so the response is disposed when the stream pipe finishes.
            var responseStream = new HttpResponseMessageStream(response, innerStream);

            var inferenceResponse = new InferenceResponse
            {
                StatusCode = statusCode,
                ContentType = contentType,
                Body = responseStream,
                // BodyDrained is set after the tap stream wraps responseStream
            };

            // Tap the SSE stream to count tokens incrementally
            var tapStream = new StreamingTokenTapStream(responseStream, inferenceResponse);

            return new InferenceResponse
            {
                StatusCode = statusCode,
                ContentType = contentType,
                Body = tapStream,
                BodyDrained = responseStream.Drained
            };
        }

        using var response2 = await SharedHttp.PostAsync(url, httpContent, ct)
            .ConfigureAwait(false);

        var contentType2 = response2.Content.Headers.ContentType?.MediaType ?? "application/json";
        var statusCode2 = (int)response2.StatusCode;

        var bodyStr = await response2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var tokens = 0;
        var serverTps = 0.0;
        if (statusCode2 >= 200 && statusCode2 < 300)
        {
            tokens = TryParseCompletionTokens(bodyStr);
            serverTps = TryParseServerTokensPerSec(bodyStr);
            if (tokens == 0)
                _logger.LogWarning("Inference response for model {Model} on port {Port} returned no usage.completion_tokens; benchmark metrics will be n/a", request.ModelName, port);
        }
        return new InferenceResponse
        {
            StatusCode = statusCode2,
            ContentType = contentType2,
            Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bodyStr)),
            TokensGenerated = tokens,
            ServerTokensPerSec = serverTps
        };
    }
}