using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IContainerRegistry
{
    Task<IReadOnlyList<RegisteredContainer>> ListAllAsync(CancellationToken ct = default);
    Task<RegisteredContainer?> GetAsync(string id, CancellationToken ct = default);
    Task<RegisteredContainer> CreateAsync(RegisteredContainer container, CancellationToken ct = default);
    Task<RegisteredContainer> UpdateAsync(string id, RegisteredContainer container, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default);
    Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default);
    Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default);
}
