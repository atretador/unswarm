using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Contracts;

/// <summary>
/// CRUD + validation for managed API keys. Backed by <see cref="UnswarmDbContext"/>.
/// The raw secret is generated, hashed, and stored; only the returned
/// <see cref="CreateApiKeyResponse.Secret"/> (once) and the stored hash are known to
/// the server.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>
    /// Create a key. <paramref name="explicitKey"/> (used only by the static-key
    /// migration) lets the caller supply the plaintext; otherwise a fresh random
    /// key is generated. Returns the secret exactly once. When
    /// <paramref name="boundAgentName"/> is non-empty the key is permanently bound
    /// to that agent name (agent keys); it must be null/empty for unbound keys.
    /// </summary>
    Task<CreateApiKeyResponse> CreateAsync(string name, ApiKeyScope scope = ApiKeyScope.Inference, string? explicitKey = null, string? boundAgentName = null, CancellationToken ct = default);

    /// <summary>All keys, secret-free (prefix only). Ordered newest first.</summary>
    Task<IReadOnlyList<ApiKeyItem>> ListAsync(CancellationToken ct = default);

    Task<ApiKeyItem?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Soft-delete (marks inactive); a revoked key can never authenticate again.</summary>
    Task<bool> RevokeAsync(string id, CancellationToken ct = default);

    /// <summary>Issue a brand-new secret for an existing key, keeping its name/scope/age.</summary>
    Task<CreateApiKeyResponse> RotateAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Validate a presented plaintext secret against stored hashes. Returns the active
    /// key entity that matches (or null). Called by the auth middleware.
    /// </summary>
    Task<ApiKeyEntity?> AuthenticateAsync(string presentedSecret, CancellationToken ct = default);

    /// <summary>
    /// Whether any key (active or retired) of the given scope exists. The scope is
    /// enforced as soon as one exists; revoking a key never reopens the surface.
    /// </summary>
    Task<bool> HasAnyAsync(ApiKeyScope scope, CancellationToken ct = default);

    /// <summary>Stamp <see cref="ApiKeyEntity.LastUsedAt"/> after a successful authenticate.</summary>
    Task UpdateLastUsedAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Resolve the agent-name binding for an agent-scope key during the /ws/agent
    /// handshake. A key already bound to X only allows the claim "X". An unbound
    /// key atomically consumes its first use: it binds to
    /// <paramref name="claimedAgentName"/> (persisted immediately; concurrent
    /// first-use races resolve to exactly one winner) and allows that claim.
    /// </summary>
    Task<AgentKeyBindingResult> ResolveAgentBindingAsync(string keyId, string claimedAgentName, CancellationToken ct = default);

    /// <summary>
    /// Reads the parsed per-key access restrictions for <paramref name="keyId"/>,
    /// or null when the key does not exist. Missing/default JSON means unrestricted.
    /// </summary>
    Task<KeyAccess?> GetAccessAsync(string keyId, CancellationToken ct = default);

    /// <summary>
    /// Cached variant of <see cref="GetAccessAsync"/> for hot-path callers: serves
    /// the parsed <see cref="KeyAccess"/> from an in-memory cache (5-minute TTL
    /// fallback) that is invalidated by <see cref="SaveAccessAsync"/>.
    /// </summary>
    Task<KeyAccess?> GetAccessCachedAsync(string keyId, CancellationToken ct = default);

    /// <summary>
    /// Persists the parsed per-key access restrictions for <paramref name="keyId"/>.
    /// Returns the stored value, or null when the key does not exist.
    /// </summary>
    Task<KeyAccess?> SaveAccessAsync(string keyId, KeyAccess access, CancellationToken ct = default);
}
