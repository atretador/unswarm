using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeModelRegistry : IModelRegistry
{
    private readonly Dictionary<string, ModelDefinition> _models = new();

    public List<ModelDefinition> CreatedModels { get; } = [];
    public List<string> DeletedModelIds { get; } = [];

    public Task<IReadOnlyList<ModelDefinition>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelDefinition>>(_models.Values.OrderBy(m => m.Name).ToList());

    public Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default)
    {
        _models.TryGetValue(id, out var model);
        return Task.FromResult(model);
    }

    public Task<ModelDefinition> CreateAsync(ModelDefinition definition, CancellationToken ct = default)
    {
        _models[definition.Id] = definition;
        CreatedModels.Add(definition);
        return Task.FromResult(definition);
    }

    public Task<ModelDefinition> UpdateAsync(string id, ModelDefinition definition, CancellationToken ct = default)
    {
        _models[id] = definition;
        return Task.FromResult(definition);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _models.Remove(id);
        DeletedModelIds.Add(id);
        return Task.CompletedTask;
    }

    public Task ValidateAsync(string id, CancellationToken ct = default)
        => Task.CompletedTask;
}
