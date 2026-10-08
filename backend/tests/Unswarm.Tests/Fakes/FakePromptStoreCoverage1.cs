using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IPromptStore"/> for coverage tests that need
/// prompt-version lookups (real fakes in Fakes/ return null always). Seed
/// entries and versions directly.
/// </summary>
public sealed class FakePromptStoreCoverage1 : IPromptStore
{
    private readonly Dictionary<string, PromptEntry> _prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string PromptId, int Version), PromptVersion> _versions = new();

    public void SeedEntry(PromptEntry entry) => _prompts[entry.Id] = entry;

    public void SeedVersion(PromptVersion version) => _versions[(version.PromptId, version.Version)] = version;

    public Task<IReadOnlyList<PromptEntry>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PromptEntry>>(
            _prompts.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<PromptEntry?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_prompts.TryGetValue(id, out var entry) ? entry : null);

    public Task<PromptEntry> CreateAsync(string name, string text, int? maxTokens = null, CancellationToken ct = default)
    {
        var entry = new PromptEntry
        {
            Id = $"p-{_prompts.Count + 1}",
            Name = name,
            Text = text,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _prompts[entry.Id] = entry;
        return Task.FromResult(entry);
    }

    public Task<PromptEntry?> UpdateAsync(string id, string name, string text, int? maxTokens = null, CancellationToken ct = default)
    {
        if (!_prompts.TryGetValue(id, out var existing))
            return Task.FromResult<PromptEntry?>(null);
        var updated = existing with { Name = name, Text = text, UpdatedAt = DateTimeOffset.UtcNow };
        _prompts[id] = updated;
        return Task.FromResult<PromptEntry?>(updated);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_prompts.Remove(id));

    public Task<PromptEntry?> SetDefaultAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_prompts.TryGetValue(id, out var entry) ? entry : null);

    public Task<PromptEntry?> GetDefaultAsync(CancellationToken ct = default)
        => Task.FromResult(_prompts.Values.FirstOrDefault(p => p.IsDefault));

    public Task<IReadOnlyList<PromptVersion>> ListVersionsAsync(string promptId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PromptVersion>>(
            _versions.Values
                .Where(v => v.PromptId == promptId)
                .OrderByDescending(v => v.Version)
                .ToList());

    public Task<PromptVersion?> GetVersionAsync(string promptId, int version, CancellationToken ct = default)
        => Task.FromResult(_versions.TryGetValue((promptId, version), out var v) ? v : null);
}
