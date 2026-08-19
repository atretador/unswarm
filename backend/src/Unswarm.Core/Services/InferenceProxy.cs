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

        // Remote targets: the container lookup below works over the agent, but the HTTP
        // inference hop must be tunneled over the agent WebSocket. That is Phase 4.2 —
        // until then, remote inference returns 501 so callers fail loudly, not silently.
        if (targetId != ExecutionTarget.HostId)
        {
            return await InvokeRemoteAsync(request, targetId, controller, ct).ConfigureAwait(false);
        }

        // First try: container registry lookup — find the running container that serves this model
        if (_containerRegistry is not null)
        {
            var registeredContainerId = await _containerRegistry
                .GetContainerIdForModelAsync(request.ModelName, ct).ConfigureAwait(false);

            if (registeredContainerId is not null)
            {
                var modelIds = await _containerRegistry
                    .GetModelIdsForContainerAsync(registeredContainerId, ct).ConfigureAwait(false);

                if (modelIds.Contains(request.ModelName))
                {
                    // Find the running Docker container for this registered container
                    var containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
                    var container = containers.FirstOrDefault(c =>
                        c.RegisteredContainerId == registeredContainerId
                        && c.Status == ContainerStatus.Running
                        && c.Port.HasValue);

                    if (container?.Port is not null)
                    {
                        var port = container.Port.Value;
                        await _healthChecker.WaitForReadyAsync(port, ct).ConfigureAwait(false);
                        return await ProxyToPortAsync(request, port, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        // Fallback: legacy model-name label path for standalone models
        var allContainers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
        var legacyContainer = allContainers.FirstOrDefault(c =>
            c.ModelName == request.ModelName && c.Status == ContainerStatus.Running && c.Port.HasValue);

        if (legacyContainer?.Port is null)
        {
            _logger.LogWarning("No running container found for model {Model}", request.ModelName);
            return new InferenceResponse { StatusCode = 503, ContentType = "text/plain" };
        }

        var legacyPort = legacyContainer.Port.Value;
        await _healthChecker.WaitForReadyAsync(legacyPort, ct).ConfigureAwait(false);
        return await ProxyToPortAsync(request, legacyPort, ct).ConfigureAwait(false);
    }

    private async Task<InferenceResponse> InvokeRemoteAsync(
        InferenceRequest request,
        string targetId,
        IDockerController controller,
        CancellationToken ct)
    {
        // Container lookup works via the remote controller (ListContainersAsync over the agent).
        // The actual inference HTTP hop will be tunneled over the agent WebSocket in Phase 4.2.
        var containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
        var container = containers.FirstOrDefault(c =>
            c.Status == ContainerStatus.Running && c.Port.HasValue);

        _logger.LogWarning(
            "Remote inference for model {Model} on target {Target} is not yet implemented (Phase 4.2); " +
            "found container {ContainerId}",
            request.ModelName, targetId, container?.Id ?? "(none)");

        return new InferenceResponse
        {
            StatusCode = 501,
            ContentType = "text/plain"
        };
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