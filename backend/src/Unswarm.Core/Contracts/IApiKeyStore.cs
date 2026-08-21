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
    /// key is generated. Returns the secret exactly once.
    /// </summary>
    Task<CreateApiKeyResponse> CreateAsync(string name, ApiKeyScope scope = ApiKeyScope.Inference, string? explicitKey = null, CancellationToken ct = default);

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
}
