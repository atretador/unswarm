using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

/// <summary>
/// CRUD implementation for cloud LLM providers.
/// API keys are encrypted at rest via <see cref="IApiKeyEncryptor"/> (DataProtection).
/// </summary>
public sealed class CloudProviderStore : ICloudProviderStore
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly IApiKeyEncryptor _encryptor;
    private readonly ILogger<CloudProviderStore> _logger;

    public CloudProviderStore(
        Func<UnswarmDbContext> dbFactory,
        IApiKeyEncryptor encryptor,
        ILogger<CloudProviderStore> logger)
    {
        _dbFactory = dbFactory;
        _encryptor = encryptor;
        _logger = logger;
    }

    public async Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default)
    {
        await CreateAsync(name, baseUrl, apiKeyPlaintext, apiKeyHint, authType: 0, ct);
    }

    public async Task CreateAsync(string name, string baseUrl, string? apiKeyPlaintext, string apiKeyHint, int authType, CancellationToken ct = default)
    {
        if (await NameExistsInDbAsync(name, ct))
            throw new InvalidOperationException($"Provider with name '{name}' already exists.");

        var encrypted = !string.IsNullOrEmpty(apiKeyPlaintext)
            ? _encryptor.Protect(apiKeyPlaintext)
            : string.Empty;

        await using var db = _dbFactory();
        var now = DateTimeOffset.UtcNow;
        var entity = new CloudProviderEntity
        {
            Id = "cp_" + Guid.NewGuid().ToString("N")[..8],
            Name = name,
            BaseUrl = baseUrl,
            ApiKeyCiphertext = encrypted,
            ApiKeyHint = apiKeyHint,
            ModelsJson = "[]",
            AuthType = authType,
            AccessTokenCiphertext = string.Empty,
            RefreshTokenCiphertext = string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CloudProviders.Add(entity);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Created cloud provider {Name} ({Id}) authType={AuthType}", name, entity.Id, authType);
    }

    public async Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Cloud provider '{id}' not found.");

        entity.BaseUrl = baseUrl;

        if (!string.IsNullOrWhiteSpace(apiKeyPlaintext))
        {
            entity.ApiKeyCiphertext = _encryptor.Protect(apiKeyPlaintext);
            if (!string.IsNullOrWhiteSpace(apiKeyHint))
                entity.ApiKeyHint = apiKeyHint;
        }
        // else: keep existing key (empty field = unchanged)

        if (!string.IsNullOrWhiteSpace(apiKeyHint))
            entity.ApiKeyHint = apiKeyHint;

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Updated cloud provider {Id}", id);
    }

    public async Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entities = await db.CloudProviders
            .OrderBy(cp => cp.Name)
            .ToListAsync(ct);

        return entities.Select(MapToList).ToList();
    }

    public async Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct);
        return entity is null ? null : MapToRead(entity);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct);
        if (entity is null)
            return false;

        db.CloudProviders.Remove(entity);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted cloud provider {Id}", id);
        return true;
    }

    public async Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct);
        if (entity is null)
            return null;

        try
        {
            return _encryptor.Unprotect(entity.ApiKeyCiphertext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt API key for provider {Id} — key ring may be unavailable", id);
            throw;
        }
    }

    public async Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default)
    {
        ValidateModelIds(modelIds);

        await using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Cloud provider '{id}' not found.");

        entity.ModelsJson = JsonSerializer.Serialize(modelIds);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Saved {Count} models for provider {Id}", modelIds.Count, id);
    }

    public async Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.CloudProviders
            .FirstOrDefaultAsync(cp => cp.Name == name, ct);
        return entity is null ? null : MapToRead(entity);
    }

    public async Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
    {
        return await NameExistsInDbAsync(name, ct);
    }

    public async Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct);
        if (entity is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(entity.ModelsJson);
            return doc.RootElement.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveOAuthTokensAsync(string id, string accessTokenCiphertext, string refreshTokenCiphertext, DateTimeOffset? expiresAt, string? chatgptAccountId, CancellationToken ct)
    {
        using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Cloud provider {id} not found");
        entity.AccessTokenCiphertext = accessTokenCiphertext;
        entity.RefreshTokenCiphertext = refreshTokenCiphertext;
        entity.TokenExpiresAt = expiresAt;
        entity.ChatgptAccountId = chatgptAccountId;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<OAuthTokenSet?> GetOAuthTokensAsync(string id, CancellationToken ct)
    {
        using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct);
        if (entity is null) return null;
        return new OAuthTokenSet(
            entity.AccessTokenCiphertext,
            entity.RefreshTokenCiphertext,
            entity.TokenExpiresAt,
            entity.ChatgptAccountId);
    }

    public async Task<int> GetAuthTypeAsync(string id, CancellationToken ct)
    {
        using var db = _dbFactory();
        var entity = await db.CloudProviders.FindAsync([id], ct);
        return entity?.AuthType ?? 0;
    }

    // ── Internal helpers ──────────────────────────────────────────

    private async Task<bool> NameExistsInDbAsync(string name, CancellationToken ct)
    {
        await using var db = _dbFactory();
        return await db.CloudProviders.AnyAsync(cp => cp.Name == name, ct);
    }

    /// <summary>Validate model id entries per the design spec.</summary>
    private static void ValidateModelIds(IReadOnlyList<string> modelIds)
    {
        if (modelIds.Count > 500)
            throw new ArgumentException($"Too many models: {modelIds.Count} (max 500).");

        var totalBytes = 0;
        for (var i = 0; i < modelIds.Count; i++)
        {
            var id = modelIds[i];
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException($"Model id at index {i} is empty.");
            if (id.StartsWith("cloud/", StringComparison.Ordinal))
                throw new ArgumentException($"Model id '{id}' at index {i} must not start with 'cloud/'.");
            totalBytes += id.Length;
        }
        if (totalBytes > 65536)
            throw new ArgumentException($"Total model id size exceeds 64 KiB ({totalBytes} bytes).");
    }

    private static CloudProviderListItem MapToList(CloudProviderEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        BaseUrl = e.BaseUrl,
        ApiKeyHint = e.ApiKeyHint,
        ModelCount = e.ModelsJson.Length > 2
            ? CountModels(e.ModelsJson)
            : 0,
        AuthType = e.AuthType,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };

    private static CloudProviderReadItem MapToRead(CloudProviderEntity e)
    {
        var item = MapToList(e);
        return new CloudProviderReadItem
        {
            Id = item.Id,
            Name = item.Name,
            BaseUrl = item.BaseUrl,
            BaseUrlFull = item.BaseUrl,
            ApiKeyHint = item.ApiKeyHint,
            ModelCount = item.ModelCount,
            AuthType = item.AuthType,
            ChatgptAccountId = e.ChatgptAccountId,
            TokenExpiresAt = e.TokenExpiresAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
        };
    }

    private static int CountModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetArrayLength();
        }
        catch
        {
            return 0;
        }
    }
}
