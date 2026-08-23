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
    private readonly IContainerRegistry _containerRegistry;
    private readonly ICloudProviderStore _cloudProviderStore;

    public ModelsController(IModelRegistry registry, IBenchmarkHistory benchmarks, IContainerRegistry containerRegistry, ICloudProviderStore cloudProviderStore)
    {
        _registry = registry;
        _benchmarks = benchmarks;
        _containerRegistry = containerRegistry;
        _cloudProviderStore = cloudProviderStore;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var models = await _registry.ListAllAsync(ct);

        // Batch-load runtime names for models that have a source runtime
        var runtimeIds = models
            .Where(m => !string.IsNullOrEmpty(m.SourceRuntimeId))
            .Select(m => m.SourceRuntimeId!)
            .Distinct()
            .ToList();
        var runtimeInfoMap = new Dictionary<string, (string Name, string Agent)>();
        foreach (var rid in runtimeIds)
        {
            var rt = await _containerRegistry.GetAsync(rid, ct).ConfigureAwait(false);
            if (rt is not null)
                runtimeInfoMap[rid] = (string.IsNullOrEmpty(rt.DisplayName) ? rt.Image : rt.DisplayName, rt.Agent);
        }

        var responses = new List<ModelResponse>(models.Count);
        foreach (var model in models)
        {
            var last = await _benchmarks.GetLatestForModelAsync(model.Id, ct).ConfigureAwait(false);
            var response = ModelResponse.FromDefinition(model, last is null ? null : LastBenchmarkResponse.From(last));
            if (!string.IsNullOrEmpty(model.SourceRuntimeId) && runtimeInfoMap.TryGetValue(model.SourceRuntimeId, out var rtInfo))
            {
                response.SourceRuntimeName = rtInfo.Name;
                response.SourceRuntimeAgent = rtInfo.Agent;
            }
            responses.Add(response);
        }

        // Append cloud models from registered providers
        var providers = await _cloudProviderStore.ListAsync(ct);
        foreach (var provider in providers)
        {
            var modelIds = await _cloudProviderStore.GetModelIdsAsync(provider.Id, ct);
            foreach (var modelId in modelIds)
            {
                responses.Add(new ModelResponse
                {
                    Id = $"cloud/{provider.Name}/{modelId}",
                    Name = modelId,
                    Origin = "cloud",
                    ProviderName = provider.Name,
                    Status = ModelStatus.Ready,
                    CreatedAt = provider.CreatedAt,
                    UpdatedAt = provider.UpdatedAt
                });
            }
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
