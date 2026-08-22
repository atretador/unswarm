using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeContainerRegistry : IContainerRegistry
{
    private readonly Dictionary<string, RegisteredRuntime> _containers = new();
    private readonly Dictionary<string, HashSet<string>> _modelMappings = new();
    private readonly Dictionary<string, string> _modelToContainer = new();

    public List<RegisteredRuntime> CreatedContainers { get; } = [];
    public List<string> DeletedContainerIds { get; } = [];

    public Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default)
    {
        var list = _containers.Values.OrderBy(c => c.DisplayName).ToList();
        return Task.FromResult<IReadOnlyList<RegisteredRuntime>>(list);
    }

    public Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default)
    {
        _containers.TryGetValue(id, out var container);
        return Task.FromResult(container);
    }

    public Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default)
    {
        _containers[container.Id] = container;
        _modelMappings[container.Id] = [];
        CreatedContainers.Add(container);
        return Task.FromResult(container);
    }

    public Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default)
    {
        _containers[id] = container;
        return Task.FromResult(container);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _containers.Remove(id);
        _modelMappings.Remove(id);
        DeletedContainerIds.Add(id);
        return Task.CompletedTask;
    }

    public Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
    {
        if (!_modelMappings.TryGetValue(registeredContainerId, out var set))
        {
            set = [];
            _modelMappings[registeredContainerId] = set;
        }
        set.Add(modelId);
        _modelToContainer[modelId] = registeredContainerId;
        return Task.CompletedTask;
    }

    public Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
    {
        if (_modelMappings.TryGetValue(registeredContainerId, out var set))
            set.Remove(modelId);
        _modelToContainer.Remove(modelId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default)
    {
        if (_modelMappings.TryGetValue(registeredContainerId, out var set))
            return Task.FromResult<IReadOnlyList<string>>(set.ToList());
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default)
    {
        _modelToContainer.TryGetValue(modelName, out var containerId);
        return Task.FromResult(containerId);
    }

    public Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(
        string idA, IReadOnlyList<string> newCanRunAlongWithA,
        string idB, IReadOnlyList<string> newCanRunAlongWithB,
        CancellationToken ct = default)
    {
        if (!_containers.TryGetValue(idA, out var containerA) || !_containers.TryGetValue(idB, out var containerB))
            return Task.FromResult<(RegisteredRuntime A, RegisteredRuntime B)?>(null);

        var updatedA = containerA with { CanRunAlongWith = newCanRunAlongWithA.ToList() };
        var updatedB = containerB with { CanRunAlongWith = newCanRunAlongWithB.ToList() };
        _containers[idA] = updatedA;
        _containers[idB] = updatedB;
        return Task.FromResult<(RegisteredRuntime A, RegisteredRuntime B)?>((updatedA, updatedB));
    }
}
