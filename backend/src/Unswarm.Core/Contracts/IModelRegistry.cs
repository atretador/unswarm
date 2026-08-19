using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IModelRegistry
{
    Task<IReadOnlyList<ModelDefinition>> ListAllAsync(CancellationToken ct = default);
    Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default);
    Task<ModelDefinition> CreateAsync(ModelDefinition definition, CancellationToken ct = default);
    Task<ModelDefinition> UpdateAsync(string id, ModelDefinition definition, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task ValidateAsync(string id, CancellationToken ct = default);
}
