using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services.Benchmarks;

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

    public async Task<PromptEntry> CreateAsync(string name, string text, int? maxTokens = null, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var now = DateTimeOffset.UtcNow;
        var entity = new PromptEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Text = text,
            MaxTokens = BenchmarkDefaults.NormalizeMaxTokens(maxTokens),
            CurrentVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Prompts.Add(entity);

        var versionEntity = new PromptVersionEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            PromptId = entity.Id,
            Version = 1,
            Text = text,
            CreatedAt = now
        };
        db.PromptVersions.Add(versionEntity);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<PromptEntry?> UpdateAsync(string id, string name, string text, int? maxTokens = null, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Prompts.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null)
            return null;

        entity.Name = name;
        var textChanged = !string.Equals(entity.Text, text, StringComparison.Ordinal);
        entity.Text = text;
        if (maxTokens is not null)
            entity.MaxTokens = BenchmarkDefaults.NormalizeMaxTokens(maxTokens);
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        if (textChanged)
        {
            entity.CurrentVersion++;
            var versionEntity = new PromptVersionEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                PromptId = entity.Id,
                Version = entity.CurrentVersion,
                Text = text,
                CreatedAt = entity.UpdatedAt
            };
            db.PromptVersions.Add(versionEntity);
        }

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
        IsDefault = e.IsDefault,
        MaxTokens = e.MaxTokens,
        CurrentVersion = e.CurrentVersion,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };

    public async Task<PromptEntry?> SetDefaultAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Prompts.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null)
            return null;

        var all = await db.Prompts.ToListAsync(ct).ConfigureAwait(false);
        foreach (var p in all)
            p.IsDefault = false;

        entity.IsDefault = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<PromptEntry?> GetDefaultAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Prompts.FirstOrDefaultAsync(p => p.IsDefault, ct).ConfigureAwait(false);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<PromptVersion>> ListVersionsAsync(string promptId, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entities = await db.PromptVersions
            .Where(v => v.PromptId == promptId)
            .ToListAsync(ct).ConfigureAwait(false);
        return entities
            .OrderByDescending(v => v.Version)
            .Select(MapVersion)
            .ToList();
    }

    public async Task<PromptVersion?> GetVersionAsync(string promptId, int version, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.PromptVersions
            .FirstOrDefaultAsync(v => v.PromptId == promptId && v.Version == version, ct)
            .ConfigureAwait(false);
        return entity is null ? null : MapVersion(entity);
    }

    private static PromptVersion MapVersion(PromptVersionEntity e) => new()
    {
        Id = e.Id,
        PromptId = e.PromptId,
        Version = e.Version,
        Text = e.Text,
        CreatedAt = e.CreatedAt
    };
}
