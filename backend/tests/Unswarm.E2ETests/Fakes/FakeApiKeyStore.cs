using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.E2ETests.Fakes;

/// <summary>
/// In-memory API key store pre-seeded with an inference-scope key so E2E tests can
/// authenticate against /v1 via the X-Api-Key header.
/// </summary>
public sealed class FakeApiKeyStore : IApiKeyStore
{
    public const string InferenceKeySecret = "e2e-inference-key";

    private readonly Dictionary<string, ApiKeyEntity> _bySecret = new(StringComparer.Ordinal);
    private int _nextId;

    public FakeApiKeyStore()
    {
        Seed(InferenceKeySecret, "E2E inference key", ApiKeyScope.Inference);
    }

    private void Seed(string secret, string name, ApiKeyScope scope, string? boundAgentName = null)
    {
        var entity = new ApiKeyEntity
        {
            Id = $"key-{Interlocked.Increment(ref _nextId)}",
            Name = name,
            KeyHash = secret,
            KeyPrefix = secret[..Math.Min(6, secret.Length)],
            Scope = scope,
            IsActive = true,
            BoundAgentName = boundAgentName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _bySecret[secret] = entity;
    }

    public Task<CreateApiKeyResponse> CreateAsync(
        string name, ApiKeyScope scope = ApiKeyScope.Inference, string? explicitKey = null,
        string? boundAgentName = null, CancellationToken ct = default)
    {
        var secret = explicitKey ?? Guid.NewGuid().ToString("N");
        Seed(secret, name, scope, boundAgentName);
        var entity = _bySecret[secret];
        return Task.FromResult(new CreateApiKeyResponse
        {
            Id = entity.Id,
            Name = entity.Name,
            KeyPrefix = entity.KeyPrefix,
            Scope = entity.Scope,
            Secret = secret
        });
    }

    public Task<IReadOnlyList<ApiKeyItem>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ApiKeyItem> items = _bySecret.Values.Select(e => (ApiKeyItem)new ApiKeyItem
        {
            Id = e.Id,
            Name = e.Name,
            KeyPrefix = e.KeyPrefix,
            Scope = e.Scope,
            BoundAgentName = e.BoundAgentName,
            CreatedAt = e.CreatedAt,
            LastUsedAt = e.LastUsedAt
        }).ToList();
        return Task.FromResult(items);
    }

    public Task<ApiKeyItem?> GetAsync(string id, CancellationToken ct = default)
    {
        ApiKeyItem? item = _bySecret.Values
            .Where(e => e.Id == id)
            .Select(e => new ApiKeyItem
            {
                Id = e.Id, Name = e.Name, KeyPrefix = e.KeyPrefix, Scope = e.Scope,
                CreatedAt = e.CreatedAt, LastUsedAt = e.LastUsedAt
            })
            .FirstOrDefault();
        return Task.FromResult(item);
    }

    public Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        var any = false;
        foreach (var kv in _bySecret.Where(kv => kv.Value.Id == id).ToList())
        {
            kv.Value.IsActive = false;
            _bySecret.Remove(kv.Key);
            any = true;
        }
        return Task.FromResult(any);
    }

    public Task<CreateApiKeyResponse> RotateAsync(string id, CancellationToken ct = default)
        => throw new NotSupportedException("Rotation is not exercised by E2E tests.");

    public Task<ApiKeyEntity?> AuthenticateAsync(string presentedSecret, CancellationToken ct = default)
    {
        _bySecret.TryGetValue(presentedSecret, out var entity);
        if (entity is { IsActive: false }) entity = null;
        return Task.FromResult(entity);
    }

    public Task<bool> HasAnyAsync(ApiKeyScope scope, CancellationToken ct = default)
        => Task.FromResult(_bySecret.Values.Any(e => e.Scope == scope));

    public Task UpdateLastUsedAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<AgentKeyBindingResult> ResolveAgentBindingAsync(string keyId, string claimedAgentName, CancellationToken ct = default)
        => Task.FromResult(AgentKeyBindingResult.Allowed);

    public Task<KeyAccess?> GetAccessAsync(string keyId, CancellationToken ct = default)
    {
        var entity = _bySecret.Values.FirstOrDefault(e => e.Id == keyId);
        return Task.FromResult(entity is null ? null : new KeyAccess());
    }

    public Task<KeyAccess?> GetAccessCachedAsync(string keyId, CancellationToken ct = default)
        => GetAccessAsync(keyId, ct);

    public Task<KeyAccess?> SaveAccessAsync(string keyId, KeyAccess access, CancellationToken ct = default)
        => Task.FromResult(_bySecret.Values.Any(e => e.Id == keyId) ? access : null);
}
