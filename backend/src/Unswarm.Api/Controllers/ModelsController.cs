using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ModelsController : ControllerBase
{
    private readonly IModelRegistry _registry;

    public ModelsController(IModelRegistry registry) => _registry = registry;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var models = await _registry.ListAllAsync(ct);
        return Ok(models.Select(ModelResponse.FromDefinition).ToList());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var model = await _registry.GetAsync(id, ct);
        return model is null ? NotFound() : Ok(ModelResponse.FromDefinition(model));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ModelCreateRequest request, CancellationToken ct)
    {
        var definition = new ModelDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Family = request.Family,
            ParameterSize = request.ParameterSize,
            Quantization = request.Quantization,
            ContextWindow = request.ContextWindow,
            ContainerImage = request.ContainerImage
        };

        var created = await _registry.CreateAsync(definition, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, ModelResponse.FromDefinition(created));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] ModelUpdateRequest request, CancellationToken ct)
    {
        var existing = await _registry.GetAsync(id, ct);
        if (existing is null) return NotFound();

        var updated = new ModelDefinition
        {
            Id = existing.Id,
            Name = request.Name ?? existing.Name,
            Family = request.Family ?? existing.Family,
            ParameterSize = request.ParameterSize ?? existing.ParameterSize,
            Quantization = request.Quantization ?? existing.Quantization,
            Status = request.Status ?? existing.Status,
            ContextWindow = request.ContextWindow ?? existing.ContextWindow,
            ContainerImage = request.ContainerImage ?? existing.ContainerImage,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt
        };

        var result = await _registry.UpdateAsync(id, updated, ct);
        return Ok(ModelResponse.FromDefinition(result));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var existing = await _registry.GetAsync(id, ct);
        if (existing is null) return NotFound();

        await _registry.DeleteAsync(id, ct);
        return NoContent();
    }
}
