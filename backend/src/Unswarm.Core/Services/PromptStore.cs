using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

/// <summary>
/// Scoped CRUD persistence for the benchmark prompt library, backed by
/// <see cref="UnswarmDbContext"/>.
/// </summary>
public sealed class PromptStore : IPromptStore
{
    private readonly Func<UnswarmDbContext> _dbFactory;

    public PromptStore(Func<UnswarmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<PromptEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        // SQLite cannot ORDER BY DateTimeOffset server-side, so materialize then sort.
        var entities = await db.Prompts
            .ToListAsync(ct).ConfigureAwait(false);
        return entities
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Map)
            .ToList();
    }

    public async Task<PromptEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Prompts.FindAsync([id], ct).ConfigureAwait(false);
        return entity is null ? null : Map(entity);
    }

    public async Task<PromptEntry> CreateAsync(string name, string text, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var now = DateTimeOffset.UtcNow;
        var entity = new PromptEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Text = text,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Prompts.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<PromptEntry?> UpdateAsync(string id, string name, string text, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Prompts.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null)
            return null;

        entity.Name = name;
        entity.Text = text;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Prompts.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null)
            return false;

        db.Prompts.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static PromptEntry Map(PromptEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Text = e.Text,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };
}
