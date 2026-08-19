using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeContainerRegistry : IContainerRegistry
{
    private readonly Dictionary<string, RegisteredContainer> _containers = new();
    private readonly Dictionary<string, HashSet<string>> _modelMappings = new();
    private readonly Dictionary<string, string> _modelToContainer = new();

    public List<RegisteredContainer> CreatedContainers { get; } = [];
    public List<string> DeletedContainerIds { get; } = [];

    public Task<IReadOnlyList<RegisteredContainer>> ListAllAsync(CancellationToken ct = default)
    {
        var list = _containers.Values.OrderBy(c => c.DisplayName).ToList();
        return Task.FromResult<IReadOnlyList<RegisteredContainer>>(list);
    }

    public Task<RegisteredContainer?> GetAsync(string id, CancellationToken ct = default)
    {
        _containers.TryGetValue(id, out var container);
        return Task.FromResult(container);
    }

    public Task<RegisteredContainer> CreateAsync(RegisteredContainer container, CancellationToken ct = default)
    {
        _containers[container.Id] = container;
        _modelMappings[container.Id] = [];
        CreatedContainers.Add(container);
        return Task.FromResult(container);
    }

    public Task<RegisteredContainer> UpdateAsync(string id, RegisteredContainer container, CancellationToken ct = default)
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
}
