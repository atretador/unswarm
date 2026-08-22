using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

public sealed class ApiKeyStore : IApiKeyStore
{
    private readonly Func<UnswarmDbContext> _dbFactory;

    public ApiKeyStore(Func<UnswarmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<CreateApiKeyResponse> CreateAsync(string name, ApiKeyScope scope = ApiKeyScope.Inference, string? explicitKey = null, string? boundAgentName = null, CancellationToken ct = default)
    {
        if (boundAgentName is not null && string.IsNullOrWhiteSpace(boundAgentName))
            throw new ArgumentException("Bound agent name must be a non-empty string when provided.", nameof(boundAgentName));

        await using var db = _dbFactory();
        var now = DateTimeOffset.UtcNow;

        string secret = explicitKey is not null ? explicitKey : GenerateSecret(scope);
        string hash = HashSecret(secret);

        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            KeyHash = hash,
            KeyPrefix = secret[..Math.Min(8, secret.Length)],
            Scope = scope,
            IsActive = true,
            BoundAgentName = string.IsNullOrWhiteSpace(boundAgentName) ? null : boundAgentName.Trim(),
            CreatedAt = now,
        };
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);

        return Map(entity, secret);
    }

    public async Task<CreateApiKeyResponse> RotateAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.ApiKeys.FindAsync([id], ct);
        if (entity is null)
            throw new KeyNotFoundException($"API key '{id}' not found.");

        string secret = GenerateSecret(entity.Scope);
        entity.KeyHash = HashSecret(secret);
        entity.KeyPrefix = secret[..Math.Min(8, secret.Length)];
        entity.IsActive = true;
        entity.LastUsedAt = null;
        await db.SaveChangesAsync(ct);

        return Map(entity, secret);
    }

    public async Task<IReadOnlyList<ApiKeyItem>> ListAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        // Materialize first: SQLite's provider can't order by DateTimeOffset
        // server-side, so sort newest-first in memory.
        var entities = await db.ApiKeys.ToListAsync(ct);
        return entities
            .OrderByDescending(k => k.CreatedAt)
            .Select(Map)
            .ToList();
    }

    public async Task<ApiKeyItem?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.ApiKeys.FindAsync([id], ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.ApiKeys.FindAsync([id], ct);
        if (entity is null)
            return false;

        entity.IsActive = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ApiKeyEntity?> AuthenticateAsync(string presentedSecret, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(presentedSecret))
            return null;

        string hash = HashSecret(presentedSecret);
        await using var db = _dbFactory();
        return await db.ApiKeys
            .FirstOrDefaultAsync(k => k.IsActive && k.KeyHash == hash, ct);
    }

    public async Task UpdateLastUsedAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.ApiKeys.FindAsync([id], ct);
        if (entity is null)
            return;

        entity.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AgentKeyBindingResult> ResolveAgentBindingAsync(string keyId, string claimedAgentName, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.ApiKeys.FindAsync([keyId], ct);
        if (entity is null || !entity.IsActive || string.IsNullOrWhiteSpace(claimedAgentName))
            return AgentKeyBindingResult.Mismatch;

        // Already bound: the claim must match exactly, forever.
        if (entity.BoundAgentName is not null)
            return entity.BoundAgentName == claimedAgentName
                ? AgentKeyBindingResult.Allowed
                : AgentKeyBindingResult.Mismatch;

        // First-use consumption: bind atomically. The single UPDATE with a
        // "BoundAgentName IS NULL" guard makes concurrent first-use races safe —
        // exactly one claimant's row update can succeed.
        var rows = await db.ApiKeys
            .Where(k => k.Id == keyId && k.BoundAgentName == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.BoundAgentName, claimedAgentName), ct);
        if (rows == 1)
            return AgentKeyBindingResult.Allowed;

        // Lost the race: re-read whoever won and compare against this claim.
        var winner = await db.ApiKeys
            .AsNoTracking()
            .Where(k => k.Id == keyId)
            .Select(k => k.BoundAgentName)
            .FirstOrDefaultAsync(ct);
        return winner == claimedAgentName
            ? AgentKeyBindingResult.Allowed
            : AgentKeyBindingResult.Mismatch;
    }

    public async Task<bool> HasAnyAsync(ApiKeyScope scope, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        // Active OR retired: a scope is enforced as soon as any key exists, so
        // revoking a key cannot reopen the surface.
        return await db.ApiKeys.AnyAsync(k => k.Scope == scope, ct);
    }

    private static string GenerateSecret(ApiKeyScope scope)
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var prefix = scope == ApiKeyScope.Agent ? "ak_" : "usk_";
        // base64url, no padding — safe in headers, query strings, and env files.
        return prefix + Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashSecret(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static CreateApiKeyResponse Map(ApiKeyEntity e, string secret) => new()
    {
        Id = e.Id,
        Name = e.Name,
        KeyPrefix = e.KeyPrefix,
        Scope = e.Scope,
        IsActive = e.IsActive,
        BoundAgentName = e.BoundAgentName,
        CreatedAt = e.CreatedAt,
        LastUsedAt = e.LastUsedAt,
        Secret = secret,
    };

    private static ApiKeyItem Map(ApiKeyEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        KeyPrefix = e.KeyPrefix,
        Scope = e.Scope,
        IsActive = e.IsActive,
        BoundAgentName = e.BoundAgentName,
        CreatedAt = e.CreatedAt,
        LastUsedAt = e.LastUsedAt,
    };
}
