using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface ISettingsStore
{
    Task<Settings> GetAsync(CancellationToken ct = default);
    Task<Settings> UpdateAsync(Settings settings, CancellationToken ct = default);
}
