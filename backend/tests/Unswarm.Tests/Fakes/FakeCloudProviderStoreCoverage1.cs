using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ICloudProviderStore"/> for coverage tests. Supports
/// providers with per-provider model metadata and captures model saves so the
/// cloud-model update path can be asserted. When <see cref="SaveModelsException"/>
/// is set, <see cref="SaveModelsAsync(string, IReadOnlyList{CloudProviderModelMeta}, CancellationToken)"/>
/// throws it.
/// </summary>
public sealed class FakeCloudProviderStoreCoverage1 : ICloudProviderStore
{
    private sealed class Provider
    {
        public string Id = "";
        public string Name = "";
        public string BaseUrl = "";
        public string ApiKeyHint = "";
        public int AuthType;
        public string? ApiKeyPlaintext;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset UpdatedAt;
        public List<CloudProviderModelMeta> Models = [];
    }

    private readonly Dictionary<string, Provider> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByName = new(StringComparer.OrdinalIgnoreCase);

    public Exception? SaveModelsException { get; set; }

    public List<(string ProviderId, IReadOnlyList<CloudProviderModelMeta> Models)> SavedModels { get; } = [];

    /// <summary>Seed a provider. Returns its id.</summary>
    public string Seed(
        string id,
        string name,
        int authType = 0,
        DateTimeOffset? createdAt = null,
        IEnumerable<CloudProviderModelMeta>? models = null)
    {
        var provider = new Provider
        {
            Id = id,
            Name = name,
            BaseUrl = "https://example.test",
            AuthType = authType,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Models = models?.ToList() ?? []
        };
        _byId[id] = provider;
        _idByName[name] = id;
        return id;
    }

    public CloudProviderModelMeta? GetMeta(string providerId, string modelId)
        => _byId.TryGetValue(providerId, out var p)
            ? p.Models.FirstOrDefault(m => m.Id == modelId)
            : null;

    public Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CreateAsync(string name, string baseUrl, string? apiKeyPlaintext, string apiKeyHint, int authType, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CloudProviderListItem> list = _byId.Values
            .Select(p => new CloudProviderListItem
            {
                Id = p.Id,
                Name = p.Name,
                BaseUrl = p.BaseUrl,
                ApiKeyHint = p.ApiKeyHint,
                ModelCount = p.Models.Count,
                AuthType = p.AuthType,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .ToList();
        return Task.FromResult(list);
    }

    public Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(id, out var p))
            return Task.FromResult<CloudProviderReadItem?>(null);
        return Task.FromResult<CloudProviderReadItem?>(ToReadItem(p));
    }

    public Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        if (!_idByName.TryGetValue(name, out var id) || !_byId.TryGetValue(id, out var p))
            return Task.FromResult<CloudProviderReadItem?>(null);
        return Task.FromResult<CloudProviderReadItem?>(ToReadItem(p));
    }

    private static CloudProviderReadItem ToReadItem(Provider p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        BaseUrl = p.BaseUrl,
        BaseUrlFull = p.BaseUrl,
        ApiKeyHint = p.ApiKeyHint,
        ModelCount = p.Models.Count,
        AuthType = p.AuthType,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt
    };

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(id, out var p))
            return Task.FromResult(false);
        _byId.Remove(id);
        _idByName.Remove(p.Name);
        return Task.FromResult(true);
    }

    public Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default)
        => SaveModelsAsync(id, modelIds.Select(CloudProviderModelMeta.FromId).ToList(), ct);

    public Task SaveModelsAsync(string id, IReadOnlyList<CloudProviderModelMeta> models, CancellationToken ct = default)
    {
        if (SaveModelsException is not null)
            throw SaveModelsException;

        SavedModels.Add((id, models));
        if (_byId.TryGetValue(id, out var p))
            p.Models = models.ToList();
        return Task.CompletedTask;
    }

    public Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
        => Task.FromResult(_idByName.ContainsKey(name));

    public Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _byId.TryGetValue(id, out var p) ? p.Models.Select(m => m.Id).ToList() : []);

    public Task<IReadOnlyList<CloudProviderModelMeta>> GetModelMetasAsync(string id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CloudProviderModelMeta>>(
            _byId.TryGetValue(id, out var p) ? p.Models.ToList() : []);

    public Task SaveOAuthTokensAsync(string id, string accessTokenCiphertext, string refreshTokenCiphertext, DateTimeOffset? expiresAt, string? chatgptAccountId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<OAuthTokenSet?> GetOAuthTokensAsync(string id, CancellationToken ct = default)
        => Task.FromResult<OAuthTokenSet?>(null);

    public Task<int> GetAuthTypeAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(id, out var p) ? p.AuthType : 0);
}
