namespace Unswarm.Core.Contracts;

/// <summary>
/// CRUD for cloud LLM provider registrations. Keys are encrypted at rest
/// via ASP.NET DataProtection — the plaintext key is never persisted.
/// </summary>
public interface ICloudProviderStore
{
    /// <summary>
    /// Create a new provider with API key auth. <paramref name="apiKeyPlaintext"/> is encrypted
    /// and stored; <paramref name="apiKeyHint"/> is captured as-is (e.g. "sk-…3f9a").
    /// </summary>
    Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default);

    /// <summary>
    /// Create a new provider with a specified auth type.
    /// When <paramref name="authType"/> is <c>0</c> (ApiKey), <paramref name="apiKeyPlaintext"/> is
    /// encrypted and stored. For other auth types the API key fields may be empty.
    /// </summary>
    Task CreateAsync(string name, string baseUrl, string? apiKeyPlaintext, string apiKeyHint, int authType, CancellationToken ct = default);

    /// <summary>
    /// Update an existing provider. When <paramref name="apiKeyPlaintext"/> is null/empty,
    /// the stored key is left unchanged. <paramref name="name"/> cannot be changed —
    /// it is immutable after creation (it becomes part of public model ids).
    /// </summary>
    Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default);

    /// <summary>All providers; key masked hint only.</summary>
    Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default);

    /// <summary>Single provider by id; key masked hint only.</summary>
    Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Delete provider; removes its model list (JSON column dies with row).</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Decrypt and return the raw API key for a provider (used by forwarding service).</summary>
    Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Validate and save a provider's model list. Entries are validated:
    /// non-empty strings, must not start with "cloud/", count ≤ 500, total ≤ 64 KiB.
    /// </summary>
    Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default);

    /// <summary>Resolve provider by its unique name (used for routing).</summary>
    Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Check if a provider name already exists (for uniqueness validation).</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);

    /// <summary>Get the model ID list for a provider.</summary>
    Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default);

    /// <summary>Save OAuth tokens for a ChatGPT subscription provider.</summary>
    Task SaveOAuthTokensAsync(string id, string accessTokenCiphertext, string refreshTokenCiphertext, DateTimeOffset? expiresAt, string? chatgptAccountId, CancellationToken ct = default);

    /// <summary>Get OAuth tokens for a provider. Returns null if not found.</summary>
    Task<OAuthTokenSet?> GetOAuthTokensAsync(string id, CancellationToken ct = default);

    /// <summary>Get the auth type for a provider (0 = ApiKey, 1 = ChatGPTSubscription).</summary>
    Task<int> GetAuthTypeAsync(string id, CancellationToken ct = default);
}

public record OAuthTokenSet(
    string AccessTokenCiphertext,
    string RefreshTokenCiphertext,
    DateTimeOffset? ExpiresAt,
    string? ChatgptAccountId);

public class CloudProviderListItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKeyHint { get; set; } = string.Empty;
    public int ModelCount { get; set; }
    public int AuthType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CloudProviderReadItem : CloudProviderListItem
{
    /// <summary>Full base URL (origin) — not masked.</summary>
    public string BaseUrlFull { get; set; } = string.Empty;
    public string? ChatgptAccountId { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}
