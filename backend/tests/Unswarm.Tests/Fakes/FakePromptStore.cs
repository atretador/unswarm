using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// In-memory IPromptStore for controller tests. Ordering by Name is approximated
/// by insertion order (the real service does the DB ordering).
/// </summary>
public sealed class FakePromptStore : IPromptStore
{
    private readonly Dictionary<string, PromptEntry> _prompts = new();
    private int _seq;

    public List<string> DeletedIds { get; } = [];

    public Task<IReadOnlyList<PromptEntry>> ListAsync(CancellationToken ct = default)
    {
        var list = _prompts.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<PromptEntry>>(list);
    }

    public Task<PromptEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        _prompts.TryGetValue(id, out var entry);
        return Task.FromResult(entry);
    }

    public Task<PromptEntry> CreateAsync(string name, string text, CancellationToken ct = default)
    {
        var entry = new PromptEntry
        {
            Id = $"prompt-{Interlocked.Increment(ref _seq)}",
            Name = name,
            Text = text,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _prompts[entry.Id] = entry;
        return Task.FromResult(entry);
    }

    public Task<PromptEntry?> UpdateAsync(string id, string name, string text, CancellationToken ct = default)
    {
        if (!_prompts.TryGetValue(id, out var existing))
            return Task.FromResult<PromptEntry?>(null);

        var updated = existing with
        {
            Name = name,
            Text = text,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _prompts[id] = updated;
        return Task.FromResult<PromptEntry?>(updated);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var deleted = _prompts.Remove(id);
        if (deleted) DeletedIds.Add(id);
        return Task.FromResult(deleted);
    }
}
