using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IRouterProfileStore
{
    Task<IReadOnlyList<RouterProfile>> ListAsync(CancellationToken ct = default);
    Task<RouterProfile?> GetAsync(string id, CancellationToken ct = default);
    Task<RouterProfile?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<RouterProfile> CreateAsync(RouterProfile profile, CancellationToken ct = default);
    Task<RouterProfile> UpdateAsync(string id, RouterProfile profile, CancellationToken ct = default);
    Task SetActiveModelIdAsync(string id, string? activeModelId, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
