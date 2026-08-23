using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests.Fakes;

/// <summary>
/// In-memory settings store seeded with an initial Settings instance. Lets tests
/// flip scheduler settings (e.g. EnableParallelSlotSkip) at runtime.
/// </summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    private Settings _settings;

    public FakeSettingsStore(Settings? initial = null)
        => _settings = initial ?? new Settings();

    public Task<Settings> GetAsync(CancellationToken ct = default)
        => Task.FromResult(_settings);

    public Task<Settings> UpdateAsync(Settings settings, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _settings, settings);
        return Task.FromResult(_settings);
    }
}
