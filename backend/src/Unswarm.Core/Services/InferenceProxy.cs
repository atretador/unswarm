using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

public sealed class InferenceProxy : IInferenceProxy
{
    private readonly IDockerController _docker;
    private readonly IDockerControllerRouter _router;
    private readonly IHealthChecker _healthChecker;
    private readonly IContainerRegistry? _containerRegistry;
    private readonly ILogger<InferenceProxy> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new(StringComparer.Ordinal);
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    public InferenceProxy(
        IDockerController docker,
        IHealthChecker healthChecker,
        ILogger<InferenceProxy> logger,
        IServiceProvider serviceProvider,
        IContainerRegistry? containerRegistry = null,
        IDockerControllerRouter? router = null)
    {
        _docker = docker;
        _healthChecker = healthChecker;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _containerRegistry = containerRegistry;
        _router = router ?? new HostOnlyDockerControllerRouter(docker);
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

        // Script runtimes: use mapped port directly (they don't appear in docker ps)
        if (registered is not null && registered.RuntimeKind == RuntimeKind.Script)
        {
            var scriptPort = registered.MappedPort ?? registered.ContainerPort;
            await _healthChecker.WaitForReadyAsync(scriptPort, 120, ct).ConfigureAwait(false);
            return await ProxyToPortAsync(request, scriptPort, ct).ConfigureAwait(false);
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
            // Skip on-demand start if the container is already starting/ready (race guard)
            if (registered.Status != ContainerRegistrationStatus.Starting &&
                registered.Status != ContainerRegistrationStatus.Ready)
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
                        // Stop any running managed containers that can't coexist with this one.
                        if (_containerRegistry is not null)
                        {
                            var allRegistered = await _containerRegistry.ListAllAsync(ct).ConfigureAwait(false);
                            var requested = allRegistered.FirstOrDefault(r => r.Id == registeredContainerId);
                            if (requested is not null)
                            {
                                var runningManaged = running.Where(c => c.RegisteredRuntimeId is not null && c.RegisteredRuntimeId != registeredContainerId).ToList();
                                foreach (var rm in runningManaged)
                                {
                                    var otherRuntime = allRegistered.FirstOrDefault(r => r.Id == rm.RegisteredRuntimeId);
                                    if (otherRuntime is null) continue;

                                    // Check coexistence: can requested run along with other?
                                    bool requestedAllowsOther = requested.CanRunAlongWith.Count == 0 ||
                                        requested.CanRunAlongWith.Any(n =>
                                            string.Equals(n, otherRuntime.Image, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(n, otherRuntime.DisplayName, StringComparison.OrdinalIgnoreCase));
                                    bool otherAllowsRequested = otherRuntime.CanRunAlongWith.Count == 0 ||
                                        otherRuntime.CanRunAlongWith.Any(n =>
                                            string.Equals(n, requested.Image, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(n, requested.DisplayName, StringComparison.OrdinalIgnoreCase));

                                    // If either side doesn't list the other, they can't coexist
                                    if (!requestedAllowsOther || !otherAllowsRequested)
                                    {
                                        _logger.LogInformation(
                                            "Stopping incompatible container {Id} ({Image}) to make room for {Requested}",
                                            rm.Id[..Math.Min(12, rm.Id.Length)], otherRuntime.Image, requested.Image);
                                        try
                                        {
                                            await _docker.StopContainerAsync(rm.Id, ct).ConfigureAwait(false);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning(ex, "Failed to stop incompatible container {Id}", rm.Id[..Math.Min(12, rm.Id.Length)]);
                                        }
                                    }
                                }
                            }
                        }

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
        }

        if (container?.Port is not { } resolvedPort)
        {
            _logger.LogWarning("No running container found for model {Model}", request.ModelName);
            return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
        }

        await _healthChecker.WaitForReadyAsync(resolvedPort, 120, ct).ConfigureAwait(false);
        return await ProxyToPortAsync(request, resolvedPort, ct).ConfigureAwait(false);
    }

    private static IReadOnlySet<string> RegisteredContainerNames(RegisteredRuntime registered)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(registered.Image)) names.Add(registered.Image);
        if (!string.IsNullOrEmpty(registered.DisplayName)) names.Add(registered.DisplayName);
        return names;
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
        if (registeredContainerId is not null && _containerRegistry is not null)
        {
            var registered = await _containerRegistry.GetAsync(registeredContainerId, ct).ConfigureAwait(false);
            if (registered is not null && registered.RuntimeKind == RuntimeKind.Script)
            {
                var scriptPort = registered.MappedPort ?? registered.ContainerPort;
                string rawScriptBody;
                try
                {
                    rawScriptBody = await remote.InferAsync(scriptPort, request.OriginalJson, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Remote script inference failed for model {Model} on target {Target} port {Port}",
                        request.ModelName, targetId, scriptPort);
                    return new InferenceResponse { StatusCode = 502, ContentType = "text/plain" };
                }

                var scriptTokens = TryParseCompletionTokens(rawScriptBody);
                return new InferenceResponse
                {
                    StatusCode = 200,
                    ContentType = "application/json",
                    Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawScriptBody)),
                    TokensGenerated = scriptTokens
                };
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

        string rawBody;
        try
        {
            rawBody = await remote.InferAsync(resolvedPort, request.OriginalJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote inference failed for model {Model} on target {Target} port {Port}",
                request.ModelName, targetId, resolvedPort);
            return new InferenceResponse { StatusCode = 502, ContentType = "text/plain" };
        }

        var tokens = TryParseCompletionTokens(rawBody);

        return new InferenceResponse
        {
            StatusCode = 200,
            ContentType = "application/json",
            Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawBody)),
            TokensGenerated = tokens
        };
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

    private async Task<InferenceResponse> ProxyToPortAsync(InferenceRequest request, int port, CancellationToken ct)
    {
        var url = request.IsStreaming
            ? $"http://127.0.0.1:{port}/v1/chat/completions"
            : $"http://127.0.0.1:{port}/v1/chat/completions";

        try
        {
            using var httpContent = new StringContent(
                request.OriginalJson,
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await SharedHttp.PostAsync(url, httpContent, ct)
                .ConfigureAwait(false);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            var statusCode = (int)response.StatusCode;

            if (request.IsStreaming)
            {
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new InferenceResponse
                {
                    StatusCode = statusCode,
                    ContentType = contentType,
                    Body = stream
                };
            }

            var bodyStr = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var tokens = 0;
            if (statusCode >= 200 && statusCode < 300)
            {
                tokens = TryParseCompletionTokens(bodyStr);
                if (tokens == 0)
                    _logger.LogWarning("Inference response for model {Model} on port {Port} returned no usage.completion_tokens; benchmark metrics will be n/a", request.ModelName, port);
            }
            return new InferenceResponse
            {
                StatusCode = statusCode,
                ContentType = contentType,
                Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bodyStr)),
                TokensGenerated = tokens
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inference invocation failed for model {Model} on port {Port}", request.ModelName, port);
            return new InferenceResponse { StatusCode = 502, ContentType = "text/plain" };
        }
    }
}