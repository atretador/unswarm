using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Catalog of every provider/runtime identity a key's access list may reference,
/// with the models each can serve. Admin-only (same surface as key management).
/// Cloud entries come from CloudProviderEntity (Name + configured model ids);
/// local entries from registered runtimes with their ContainerModelMapping model
/// lists — the same mapping resolution the scheduler uses for routing.
/// </summary>
/// <remarks>
/// GET /api/provider-model-catalog — List all providers with their models
/// </remarks>
[ApiController]
[Route("api/provider-model-catalog")]
[Authorize(Roles = "Admin")]
public sealed class ProviderModelCatalogController : ControllerBase
{
    private readonly ICloudProviderStore _cloudProviders;
    private readonly IContainerRegistry _containers;
    private readonly IRouterProfileStore _routerProfiles;
    private readonly IModelRegistry _modelRegistry;

    public ProviderModelCatalogController(ICloudProviderStore cloudProviders, IContainerRegistry containers, IRouterProfileStore routerProfiles, IModelRegistry modelRegistry)
    {
        _cloudProviders = cloudProviders;
        _containers = containers;
        _routerProfiles = routerProfiles;
        _modelRegistry = modelRegistry;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var catalog = new List<ProviderModelCatalogItem>();

        foreach (var provider in await _cloudProviders.ListAsync(ct))
        {
            var models = await _cloudProviders.GetModelIdsAsync(provider.Id, ct);
            // Cloud model IDs are already human-readable display names (e.g. "gpt-4o").
            catalog.Add(new ProviderModelCatalogItem
            {
                Name = provider.Name,
                Kind = "cloud",
                Models = [.. models],
                ModelDisplayNames = models.ToDictionary(m => m, m => m)
            });
        }

        foreach (var runtime in await _containers.ListAllAsync(ct))
        {
            var modelIds = await _containers.GetModelIdsForContainerAsync(runtime.Id, ct);
            var displayNames = new Dictionary<string, string>();
            foreach (var modelId in modelIds)
            {
                var def = await _modelRegistry.GetAsync(modelId, ct);
                displayNames[modelId] = def?.DisplayName ?? def?.Name ?? modelId;
            }

            catalog.Add(new ProviderModelCatalogItem
            {
                Name = runtime.DisplayName,
                Kind = "local",
                Models = [.. modelIds],
                ModelDisplayNames = displayNames
            });
        }

        foreach (var profile in await _routerProfiles.ListAsync(ct))
        {
            var modelIds = profile.Entries
                .Where(e => e.IsEnabled)
                .OrderBy(e => e.Priority)
                .Select(e => e.ModelId)
                .ToList();

            // Router profiles reference model IDs directly; resolve display names
            // from the registry for local models, use the ID as-is for cloud models.
            var displayNames = new Dictionary<string, string>();
            foreach (var modelId in modelIds)
            {
                if (modelId.StartsWith("cloud/", StringComparison.Ordinal))
                {
                    displayNames[modelId] = modelId;
                }
                else
                {
                    var def = await _modelRegistry.GetAsync(modelId, ct);
                    displayNames[modelId] = def?.DisplayName ?? def?.Name ?? modelId;
                }
            }

            catalog.Add(new ProviderModelCatalogItem
            {
                Name = profile.Name,
                Kind = "router",
                Models = modelIds,
                ModelDisplayNames = displayNames
            });
        }

        return Ok(catalog);
    }
}
