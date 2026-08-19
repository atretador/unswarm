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
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    public InferenceProxy(
        IDockerController docker,
        IHealthChecker healthChecker,
        ILogger<InferenceProxy> logger,
        IContainerRegistry? containerRegistry = null,
        IDockerControllerRouter? router = null)
    {
        _docker = docker;
        _healthChecker = healthChecker;
        _logger = logger;
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
        RegisteredContainer? registered = null;
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

        var containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
        var running = containers.Where(c => c.Status == ContainerStatus.Running && c.Port.HasValue).ToList();

        ContainerInfo? container = null;
        if (!string.IsNullOrEmpty(registeredContainerId))
        {
            // Match by registry label first.
            container = running.FirstOrDefault(c => c.RegisteredContainerId == registeredContainerId);

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

        if (container?.Port is not { } port)
        {
            _logger.LogWarning("No running container found for model {Model}", request.ModelName);
            return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
        }

        await _healthChecker.WaitForReadyAsync(port, ct).ConfigureAwait(false);
        return await ProxyToPortAsync(request, port, ct).ConfigureAwait(false);
    }

    private static IReadOnlySet<string> RegisteredContainerNames(RegisteredContainer registered)
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
                c.RegisteredContainerId == registeredContainerId
                && c.Status == ContainerStatus.Running
                && c.Port.HasValue);
        }

        // Fallback: match by model/container name on the agent.
        container ??= containers.FirstOrDefault(c =>
            (c.ModelName == request.ModelName || c.ModelId == request.ModelName)
            && c.Status == ContainerStatus.Running
            && c.Port.HasValue);

        if (container?.Port is not { } port)
        {
            _logger.LogWarning("No running container found for model {Model} on target {Target}", request.ModelName, targetId);
            return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
        }

        string rawBody;
        try
        {
            rawBody = await remote.InferAsync(port, request.OriginalJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote inference failed for model {Model} on target {Target} port {Port}",
                request.ModelName, targetId, port);
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

            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new InferenceResponse
            {
                StatusCode = statusCode,
                ContentType = contentType,
                Body = body
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inference invocation failed for model {Model} on port {Port}", request.ModelName, port);
            return new InferenceResponse { StatusCode = 502, ContentType = "text/plain" };
        }
    }
}