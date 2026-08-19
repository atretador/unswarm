using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ContainersController : ControllerBase
{
    private readonly IDockerController _docker;
    private readonly IModelRegistry _registry;
    private readonly IClock _clock;
    private readonly IContainerRegistrationService _registrationService;
    private readonly IContainerRegistry _containerRegistry;

    public ContainersController(
        IDockerController docker,
        IModelRegistry registry,
        IClock clock,
        IContainerRegistrationService registrationService,
        IContainerRegistry containerRegistry)
    {
        _docker = docker;
        _registry = registry;
        _clock = clock;
        _registrationService = registrationService;
        _containerRegistry = containerRegistry;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var containers = await _docker.ListContainersAsync(ct);
        return Ok(containers.Select(ContainerResponse.FromContainerInfo).ToList());
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] ContainerStartRequest request, CancellationToken ct)
    {
        var model = await _registry.GetAsync(request.ModelId, ct);
        if (model is null) return NotFound(new { error = $"Model {request.ModelId} not found" });

        var startResult = await _docker.StartContainerAsync(model.Name, ct);
        if (startResult.ErrorMessage is not null)
            return StatusCode(500, new { error = startResult.ErrorMessage });

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

    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id, CancellationToken ct)
    {
        await _docker.StopContainerAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id}/restart")]
    public async Task<IActionResult> Restart(string id, CancellationToken ct)
    {
        var result = await _docker.RestartContainerAsync(id, ct);
        if (result.ErrorMessage is not null)
            return StatusCode(500, new { error = result.ErrorMessage });

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

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] ContainerRegistrationRequestDto dto,
        CancellationToken ct)
    {
        try
        {
            var result = await _registrationService.RegisterAsync(dto.ToRequest(), ct);
            return Ok(RegisteredContainerResponse.From(result.Container, result.DiscoveredModels));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("registered")]
    public async Task<IActionResult> ListRegistered(CancellationToken ct)
    {
        var containers = await _containerRegistry.ListAllAsync(ct);
        var responses = new List<RegisteredContainerResponse>();

        foreach (var container in containers)
        {
            var modelIds = await _containerRegistry.GetModelIdsForContainerAsync(container.Id, ct);
            var models = new List<ModelDefinition>();
            foreach (var modelId in modelIds)
            {
                var model = await _registry.GetAsync(modelId, ct);
                if (model is not null)
                    models.Add(model);
            }
            responses.Add(RegisteredContainerResponse.From(container, models));
        }

        return Ok(responses);
    }

    [HttpGet("registered/{id}")]
    public async Task<IActionResult> GetRegistered(string id, CancellationToken ct)
    {
        var container = await _containerRegistry.GetAsync(id, ct);
        if (container is null)
            return NotFound();

        var modelIds = await _containerRegistry.GetModelIdsForContainerAsync(id, ct);
        var models = new List<ModelDefinition>();
        foreach (var modelId in modelIds)
        {
            var model = await _registry.GetAsync(modelId, ct);
            if (model is not null)
                models.Add(model);
        }

        return Ok(RegisteredContainerResponse.From(container, models));
    }

    [HttpPost("registered/{id}/rediscover")]
    public async Task<IActionResult> Rediscover(string id, CancellationToken ct)
    {
        try
        {
            var result = await _registrationService.RediscoverAsync(id, ct);
            return Ok(RegisteredContainerResponse.From(result.Container, result.DiscoveredModels));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("registered/{id}")]
    public async Task<IActionResult> DeleteRegistered(string id, [FromQuery] bool deleteModels = false, CancellationToken ct = default)
    {
        try
        {
            await _registrationService.DeleteAsync(id, deleteModels, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
