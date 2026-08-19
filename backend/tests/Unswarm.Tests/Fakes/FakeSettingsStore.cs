using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeSettingsStore : ISettingsStore
{
    private Settings _settings;

    public FakeSettingsStore(Settings? initial = null)
    {
        _settings = initial ?? new Settings();
    }

    public Task<Settings> GetAsync(CancellationToken ct = default)
        => Task.FromResult(_settings);

    public Task<Settings> UpdateAsync(Settings settings, CancellationToken ct = default)
    {
        _settings = settings;
        return Task.FromResult(_settings);
    }
}
