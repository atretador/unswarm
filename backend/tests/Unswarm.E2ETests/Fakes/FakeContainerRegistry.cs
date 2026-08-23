using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests.Fakes;

/// <summary>
/// In-memory container registry with model→runtime mappings. Adapted from
/// Unswarm.Tests/Fakes for the E2E host.
/// </summary>
public sealed class FakeContainerRegistry : IContainerRegistry
{
    private readonly Dictionary<string, RegisteredRuntime> _containers = new();
    private readonly Dictionary<string, string> _modelToContainer = new();

    public Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default)
    {
        lock (_containers)
        {
            IReadOnlyList<RegisteredRuntime> list = _containers.Values.OrderBy(c => c.Id).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_containers)
        {
            _containers.TryGetValue(id, out var container);
            return Task.FromResult(container);
        }
    }

    public Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default)
    {
        lock (_containers) _containers[container.Id] = container;
        return Task.FromResult(container);
    }

    public Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default)
    {
        lock (_containers) _containers[id] = container;
        return Task.FromResult(container);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_containers) _containers.Remove(id);
        return Task.CompletedTask;
    }

    public Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
    {
        lock (_modelToContainer) _modelToContainer[modelId] = registeredContainerId;
        return Task.CompletedTask;
    }

    public Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
    {
        lock (_modelToContainer) _modelToContainer.Remove(modelId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default)
    {
        lock (_modelToContainer)
        {
            IReadOnlyList<string> ids = _modelToContainer
                .Where(kv => kv.Value == registeredContainerId)
                .Select(kv => kv.Key)
                .ToList();
            return Task.FromResult(ids);
        }
    }

    public Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default)
    {
        lock (_modelToContainer)
        {
            _modelToContainer.TryGetValue(modelName, out var containerId);
            return Task.FromResult(containerId);
        }
    }

    public Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(
        string idA, IReadOnlyList<string> newCanRunAlongWithA,
        string idB, IReadOnlyList<string> newCanRunAlongWithB,
        CancellationToken ct = default)
    {
        lock (_containers)
        {
            if (!_containers.TryGetValue(idA, out var a) || !_containers.TryGetValue(idB, out var b))
                return Task.FromResult<(RegisteredRuntime A, RegisteredRuntime B)?>(null);

            a = a with { CanRunAlongWith = newCanRunAlongWithA.ToList() };
            b = b with { CanRunAlongWith = newCanRunAlongWithB.ToList() };
            _containers[idA] = a;
            _containers[idB] = b;
            return Task.FromResult<(RegisteredRuntime A, RegisteredRuntime B)?>((a, b));
        }
    }
}
