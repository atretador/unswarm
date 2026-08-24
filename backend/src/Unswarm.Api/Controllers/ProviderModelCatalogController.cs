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

    public ProviderModelCatalogController(ICloudProviderStore cloudProviders, IContainerRegistry containers)
    {
        _cloudProviders = cloudProviders;
        _containers = containers;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var catalog = new List<ProviderModelCatalogItem>();

        foreach (var provider in await _cloudProviders.ListAsync(ct))
        {
            var models = await _cloudProviders.GetModelIdsAsync(provider.Id, ct);
            catalog.Add(new ProviderModelCatalogItem
            {
                Name = provider.Name,
                Kind = "cloud",
                Models = [.. models]
            });
        }

        foreach (var runtime in await _containers.ListAllAsync(ct))
        {
            var models = await _containers.GetModelIdsForContainerAsync(runtime.Id, ct);
            catalog.Add(new ProviderModelCatalogItem
            {
                Name = runtime.DisplayName,
                Kind = "local",
                Models = [.. models]
            });
        }

        return Ok(catalog);
    }
}
