using System.Text.Json;

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
    /// Validate and save a provider's model list with metadata.
    /// Entries are validated: non-empty ids, must not start with "cloud/", count ≤ 500, total ≤ 64 KiB.
    /// </summary>
    Task SaveModelsAsync(string id, IReadOnlyList<CloudProviderModelMeta> models, CancellationToken ct = default);

    /// <summary>
    /// Save a provider's model list from bare IDs (metadata defaults to empty).
    /// Convenience overload for backward compatibility.
    /// </summary>
    Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default);

    /// <summary>Resolve provider by its unique name (used for routing).</summary>
    Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Check if a provider name already exists (for uniqueness validation).</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);

    /// <summary>Get the model ID list for a provider (bare IDs, no metadata).</summary>
    Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default);

    /// <summary>Get the full model metadata list for a provider.</summary>
    Task<IReadOnlyList<CloudProviderModelMeta>> GetModelMetasAsync(string id, CancellationToken ct = default);

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

/// <summary>
/// Metadata for a single cloud provider model. Stored as a JSON array of these
/// objects in <see cref="CloudProviderEntity.ModelsJson"/>.
/// </summary>
public sealed class CloudProviderModelMeta
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("contextWindow")]
    public int ContextWindow { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("family")]
    public string Family { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("parameterSize")]
    public string ParameterSize { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("quantization")]
    public string Quantization { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>Create a meta entry from a bare model ID (all metadata zero/empty).</summary>
    public static CloudProviderModelMeta FromId(string id) => new() { Id = id };

    public override string ToString() => Id;
}

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

/// <summary>
/// Helpers for reading <see cref="CloudProviderEntity.ModelsJson"/> in both
/// the legacy bare-string format and the current object format.
/// </summary>
public static class CloudProviderModelsJsonHelper
{
    /// <summary>
    /// Parse ModelsJson into a list of <see cref="CloudProviderModelMeta"/>.
    /// Handles both legacy <c>["id1","id2"]</c> and current <c>[{"id":"id1",...}]</c> formats.
    /// </summary>
    public static IReadOnlyList<CloudProviderModelMeta> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new List<CloudProviderModelMeta>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    // Legacy bare-string format
                    var id = item.GetString() ?? "";
                    if (!string.IsNullOrEmpty(id))
                        result.Add(CloudProviderModelMeta.FromId(id));
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var meta = new CloudProviderModelMeta();
                    if (item.TryGetProperty("id", out var idEl))
                        meta.Id = idEl.GetString() ?? "";
                    if (item.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number)
                        meta.ContextWindow = cw.GetInt32();
                    if (item.TryGetProperty("maxOutputTokens", out var mot) && mot.ValueKind == JsonValueKind.Number)
                        meta.MaxOutputTokens = mot.GetInt32();
                    if (item.TryGetProperty("family", out var fam) && fam.ValueKind == JsonValueKind.String)
                        meta.Family = fam.GetString() ?? "";
                    if (item.TryGetProperty("parameterSize", out var ps) && ps.ValueKind == JsonValueKind.String)
                        meta.ParameterSize = ps.GetString() ?? "";
                    if (item.TryGetProperty("quantization", out var q) && q.ValueKind == JsonValueKind.String)
                        meta.Quantization = q.GetString() ?? "";
                    if (item.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                        meta.DisplayName = dn.GetString() ?? "";

                    if (!string.IsNullOrEmpty(meta.Id))
                        result.Add(meta);
                }
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Extract bare model IDs from a ModelsJson string.</summary>
    public static IReadOnlyList<string> ParseIds(string json)
        => Parse(json).Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

    /// <summary>Serialize a list of <see cref="CloudProviderModelMeta"/> to ModelsJson.</summary>
    public static string Serialize(IReadOnlyList<CloudProviderModelMeta> models)
        => JsonSerializer.Serialize(models);
}
