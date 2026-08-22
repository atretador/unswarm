using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IContainerRegistry
{
    Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default);
    Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default);
    Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default);
    Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default);
    Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default);
    Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default);

    /// <summary>
    /// Atomically update two runtimes' CanRunAlongWith lists in a single DB transaction.
    /// </summary>
    Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(
        string idA, IReadOnlyList<string> newCanRunAlongWithA,
        string idB, IReadOnlyList<string> newCanRunAlongWithB,
        CancellationToken ct = default);
}
