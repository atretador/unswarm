using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class ModelsController : ControllerBase
{
    private readonly IModelRegistry _registry;
    private readonly IBenchmarkHistory _benchmarks;

    public ModelsController(IModelRegistry registry, IBenchmarkHistory benchmarks)
    {
        _registry = registry;
        _benchmarks = benchmarks;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var models = await _registry.ListAllAsync(ct);
        var responses = new List<ModelResponse>(models.Count);
        foreach (var model in models)
        {
            var last = await _benchmarks.GetLatestForModelAsync(model.Id, ct).ConfigureAwait(false);
            responses.Add(ModelResponse.FromDefinition(model, last is null ? null : LastBenchmarkResponse.From(last)));
        }
        return Ok(responses);
    }

    [HttpGet("{*id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var model = await _registry.GetAsync(id, ct);
        if (model is null && !string.IsNullOrEmpty(id) && id[0] != '/')
            model = await _registry.GetAsync("/" + id, ct).ConfigureAwait(false);
        if (model is null) return NotFound();

        var last = await _benchmarks.GetLatestForModelAsync(model.Id, ct).ConfigureAwait(false);
        return Ok(ModelResponse.FromDefinition(model, last is null ? null : LastBenchmarkResponse.From(last)));
    }

    [Authorize(Roles = "Admin")]
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

    [Authorize(Roles = "Admin")]
    [HttpPut("{*id}")]
    public async Task<IActionResult> Update(string id, [FromBody] ModelUpdateRequest request, CancellationToken ct)
    {
        var existing = await _registry.GetAsync(id, ct);
        if (existing is null && !string.IsNullOrEmpty(id) && id[0] != '/')
            existing = await _registry.GetAsync("/" + id, ct).ConfigureAwait(false);
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

        var result = await _registry.UpdateAsync(existing.Id, updated, ct);
        return Ok(ModelResponse.FromDefinition(result));
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("{*id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var existing = await _registry.GetAsync(id, ct);
        if (existing is null && !string.IsNullOrEmpty(id) && id[0] != '/')
            existing = await _registry.GetAsync("/" + id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound();

        await _registry.DeleteAsync(existing.Id, ct);
        return NoContent();
    }
}
