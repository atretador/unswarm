using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

public sealed class RouterProfileStore : IRouterProfileStore
{
    private readonly Func<UnswarmDbContext> _dbFactory;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, (RouterProfile Profile, DateTimeOffset LoadedAt)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (RouterProfile Profile, DateTimeOffset LoadedAt)> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public RouterProfileStore(Func<UnswarmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<RouterProfile>> ListAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entities = await db.RouterProfiles
            .OrderBy(rp => rp.Name)
            .ToListAsync(ct);

        return entities.Select(MapToDomain).ToList();
    }

    public async Task<RouterProfile?> GetAsync(string id, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(id, out var cached)
            && DateTimeOffset.UtcNow - cached.LoadedAt < CacheTtl)
        {
            return cached.Profile;
        }

        await using var db = _dbFactory();
        var entity = await db.RouterProfiles.FindAsync([id], ct);
        if (entity is null)
            return null;

        var profile = MapToDomain(entity);
        _cache[id] = (profile, DateTimeOffset.UtcNow);
        return profile;
    }

    public async Task<RouterProfile?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        if (_nameCache.TryGetValue(name, out var cached)
            && DateTimeOffset.UtcNow - cached.LoadedAt < CacheTtl)
        {
            return cached.Profile;
        }

        await using var db = _dbFactory();
        var entity = await db.RouterProfiles
            .FirstOrDefaultAsync(rp => rp.Name == name, ct);
        if (entity is null)
            return null;

        var profile = MapToDomain(entity);
        _nameCache[name] = (profile, DateTimeOffset.UtcNow);
        // Also populate id cache
        _cache[entity.Id] = (profile, DateTimeOffset.UtcNow);
        return profile;
    }

    public async Task<RouterProfile> CreateAsync(RouterProfile profile, CancellationToken ct = default)
    {
        if (await NameExistsAsync(profile.Name, ct))
            throw new InvalidOperationException($"Router profile with name '{profile.Name}' already exists.");

        var now = DateTimeOffset.UtcNow;
        var entity = new RouterProfileEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = profile.Name,
            Mode = profile.Mode.ToString(),
            EntriesJson = JsonSerializer.Serialize(profile.Entries, s_json),
            ActiveModelId = profile.ActiveModelId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var db = _dbFactory();
        db.RouterProfiles.Add(entity);
        await db.SaveChangesAsync(ct);

        var result = MapToDomain(entity);
        _cache[entity.Id] = (result, DateTimeOffset.UtcNow);
        _nameCache[entity.Name] = (result, DateTimeOffset.UtcNow);
        return result;
    }

    public async Task<RouterProfile> UpdateAsync(string id, RouterProfile profile, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.RouterProfiles.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Router profile '{id}' not found.");

        if (!string.Equals(entity.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
            && await NameExistsAsync(profile.Name, ct))
            throw new InvalidOperationException($"Router profile with name '{profile.Name}' already exists.");

        var oldName = entity.Name;
        entity.Name = profile.Name;
        entity.Mode = profile.Mode.ToString();
        entity.EntriesJson = JsonSerializer.Serialize(profile.Entries, s_json);
        entity.ActiveModelId = profile.ActiveModelId;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var result = MapToDomain(entity);
        _cache[id] = (result, DateTimeOffset.UtcNow);
        _nameCache[entity.Name] = (result, DateTimeOffset.UtcNow);
        if (!string.Equals(oldName, entity.Name, StringComparison.OrdinalIgnoreCase))
            _nameCache.TryRemove(oldName, out _);
        return result;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.RouterProfiles.FindAsync([id], ct);
        if (entity is null)
            throw new KeyNotFoundException($"Router profile '{id}' not found.");

        db.RouterProfiles.Remove(entity);
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(id, out _);
        _nameCache.TryRemove(entity.Name, out _);
    }

    private async Task<bool> NameExistsAsync(string name, CancellationToken ct)
    {
        await using var db = _dbFactory();
        return await db.RouterProfiles.AnyAsync(rp => rp.Name == name, ct);
    }

    private static RouterProfile MapToDomain(RouterProfileEntity e)
    {
        var entries = DeserializeEntries(e.EntriesJson);
        var mode = Enum.TryParse<RouterProfileMode>(e.Mode, ignoreCase: true, out var m)
            ? m
            : RouterProfileMode.Auto;

        return new RouterProfile
        {
            Id = e.Id,
            Name = e.Name,
            Mode = mode,
            Entries = entries,
            ActiveModelId = e.ActiveModelId,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
    }

    internal static IReadOnlyList<RouterProfileEntry> DeserializeEntries(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RouterProfileEntry>>(json, s_json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
