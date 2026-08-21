using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class ContainersController : ControllerBase
{
    private readonly IDockerController _docker;
    private readonly IModelRegistry _registry;
    private readonly IClock _clock;
    private readonly IContainerRegistrationService _registrationService;
    private readonly IContainerRegistry _containerRegistry;
    private readonly IBenchmarkHistory _benchmarks;

    public ContainersController(
        IDockerController docker,
        IModelRegistry registry,
        IClock clock,
        IContainerRegistrationService registrationService,
        IContainerRegistry containerRegistry,
        IBenchmarkHistory benchmarks)
    {
        _docker = docker;
        _registry = registry;
        _clock = clock;
        _registrationService = registrationService;
        _containerRegistry = containerRegistry;
        _benchmarks = benchmarks;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var containers = await _docker.ListContainersAsync(ct);
        return Ok(containers.Select(ContainerResponse.FromContainerInfo).ToList());
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] ContainerStartRequest request, CancellationToken ct)
    {
        var model = await _registry.GetAsync(request.ModelId, ct);
        if (model is null) return NotFound(new { error = $"Model {request.ModelId} not found" });

        var startResult = await _docker.StartContainerAsync(model.Name, ct);
        if (startResult.ErrorMessage is not null)
            return StatusCode(500, new { error = "Failed to start container" });

        var inspect = await _docker.InspectContainerAsync(startResult.ContainerId, ct);

        var response = new ContainerResponse
        {
            Id = startResult.ContainerId,
            ModelId = model.Id,
            ModelName = model.Name,
            Status = ContainerStatus.Running,
            Port = startResult.MappedPort,
            Pid = inspect?.Pid,
            MemoryMb = inspect?.MemoryMb ?? 0,
            CpuPercent = inspect?.CpuPercent ?? 0,
            Uptime = inspect?.UptimeSeconds ?? 0,
            CreatedAt = _clock.UtcNow
        };

        return Ok(response);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id, CancellationToken ct)
    {
        await _docker.StopContainerAsync(id, ct);
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{id}/restart")]
    public async Task<IActionResult> Restart(string id, CancellationToken ct)
    {
        var result = await _docker.RestartContainerAsync(id, ct);
        if (result.ErrorMessage is not null)
            return StatusCode(500, new { error = "Failed to restart container" });

        var inspect = await _docker.InspectContainerAsync(result.ContainerId, ct);

        var response = new ContainerResponse
        {
            Id = result.ContainerId,
            ModelId = "",
            ModelName = "",
            Status = ContainerStatus.Running,
            Port = result.MappedPort,
            Pid = inspect?.Pid,
            MemoryMb = inspect?.MemoryMb ?? 0,
            CpuPercent = inspect?.CpuPercent ?? 0,
            Uptime = inspect?.UptimeSeconds ?? 0,
            CreatedAt = _clock.UtcNow
        };

        return Ok(response);
    }

    // ── Container-first registration endpoints ─────────────────────────────

    [Authorize(Roles = "Admin")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRuntimeRequestDto dto,
        CancellationToken ct)
    {
        try
        {
            var result = await _registrationService.RegisterAsync(dto.ToRequest(), ct);
            return Ok(RegisteredRuntimeResponse.From(result.Container, result.DiscoveredModels));
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "Failed to register runtime" });
        }
    }

    [HttpGet("registered")]
    public async Task<IActionResult> ListRegistered(CancellationToken ct)
    {
        var containers = await _containerRegistry.ListAllAsync(ct);
        var responses = new List<RegisteredRuntimeResponse>();

        foreach (var container in containers)
        {
            responses.Add(await BuildRegisteredResponseAsync(container, ct).ConfigureAwait(false));
        }

        return Ok(responses);
    }

    [HttpGet("registered/{id}")]
    public async Task<IActionResult> GetRegistered(string id, CancellationToken ct)
    {
        var container = await _containerRegistry.GetAsync(id, ct);
        if (container is null)
            return NotFound();

        return Ok(await BuildRegisteredResponseAsync(container, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Builds a RegisteredRuntimeResponse, populating each discovered model's
    /// LastBenchmark from the model's latest persisted benchmark (same pattern as
    /// ModelsController.List/Get).
    /// </summary>
    private async Task<RegisteredRuntimeResponse> BuildRegisteredResponseAsync(RegisteredRuntime container, CancellationToken ct)
    {
        var modelIds = await _containerRegistry.GetModelIdsForContainerAsync(container.Id, ct);
        var models = new List<ModelResponse>();
        foreach (var modelId in modelIds)
        {
            var model = await _registry.GetAsync(modelId, ct);
            if (model is null)
                continue;

            var last = await _benchmarks.GetLatestForModelAsync(model.Id, ct).ConfigureAwait(false);
            models.Add(ModelResponse.FromDefinition(model, last is null ? null : LastBenchmarkResponse.From(last)));
        }

        return new RegisteredRuntimeResponse
        {
            Id = container.Id,
            DisplayName = container.DisplayName,
            Image = container.Image,
            ContainerPort = container.ContainerPort,
            Agent = container.Agent,
            CanRunAlongWith = (container.CanRunAlongWith ?? []).ToList(),
            Status = container.Status.ToString().ToLowerInvariant(),
            RuntimeContainerId = container.RuntimeContainerId,
            MappedPort = container.MappedPort,
            ErrorMessage = container.ErrorMessage,
            CreatedAt = container.CreatedAt,
            LastDiscoveredAt = container.LastDiscoveredAt,
            DiscoveredModels = models
        };
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("registered/{id}/rediscover")]
    public async Task<IActionResult> Rediscover(string id, CancellationToken ct)
    {
        try
        {
            var result = await _registrationService.RediscoverAsync(id, ct);
            return Ok(RegisteredRuntimeResponse.From(result.Container, result.DiscoveredModels));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "Registered runtime not found" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("registered/{id}/start")]
    public async Task<IActionResult> StartRegistered(string id, CancellationToken ct)
    {
        RegisteredRuntimeWithModels result;
        try
        {
            result = await _registrationService.StartAsync(id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Registered container not found" });
        }

        // Reuse the same response shape as GET /api/containers/registered/{id}
        // (discoveredModels populated with lastBenchmark).
        return Ok(await BuildRegisteredResponseAsync(result.Container, ct).ConfigureAwait(false));
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("registered/{id}")]
    public async Task<IActionResult> DeleteRegistered(string id, [FromQuery] bool deleteModels = false, CancellationToken ct = default)
    {
        try
        {
            await _registrationService.DeleteAsync(id, deleteModels, ct);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "Registered runtime not found" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("registered/{id}/concurrency")]
    public async Task<IActionResult> UpdateConcurrency(string id, [FromBody] UpdateRuntimeConcurrencyRequestDto dto, CancellationToken ct)
    {
        var incoming = dto.CanRunAlongWith ?? [];

        // Trim, drop empties, dedupe case-insensitively (OrdinalIgnoreCase).
        var cleaned = incoming
            .Select(s => s?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var container = await _registrationService.UpdateCanRunAlongWithAsync(id, cleaned, ct).ConfigureAwait(false);
        if (container is null)
            return NotFound(new { error = "Registered runtime not found" });

        return Ok(await BuildRegisteredResponseAsync(container, ct).ConfigureAwait(false));
    }
}
