using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ICloudProviderStore"/> for the ChatGPT-subscription
/// forwarding tests: seedable provider (with auth type) and OAuth token set,
/// capturing saved (refreshed) tokens.
/// </summary>
public sealed class FakeCloudProviderStoreCoverage3 : ICloudProviderStore
{
    private string _id = "prov-1";
    private string _name = "chatgpt";
    private int _authType = 1;
    private OAuthTokenSet? _tokens;

    public Exception? GetByNameException { get; set; }
    public Exception? GetTokensException { get; set; }

    public List<(string Id, string Access, string Refresh, DateTimeOffset? ExpiresAt, string? AccountId)> SavedTokens { get; } = [];

    public void Seed(string id, string name, int authType, OAuthTokenSet? tokens = null)
    {
        _id = id;
        _name = name;
        _authType = authType;
        _tokens = tokens;
    }

    public Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        if (GetByNameException is not null) throw GetByNameException;
        if (!string.Equals(name, _name, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<CloudProviderReadItem?>(null);
        return Task.FromResult<CloudProviderReadItem?>(new CloudProviderReadItem
        {
            Id = _id,
            Name = _name,
            AuthType = _authType,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public Task<int> GetAuthTypeAsync(string id, CancellationToken ct = default) => Task.FromResult(_authType);

    public Task<OAuthTokenSet?> GetOAuthTokensAsync(string id, CancellationToken ct = default)
    {
        if (GetTokensException is not null) throw GetTokensException;
        return Task.FromResult(_tokens);
    }

    public Task SaveOAuthTokensAsync(string id, string accessTokenCiphertext, string refreshTokenCiphertext, DateTimeOffset? expiresAt, string? chatgptAccountId, CancellationToken ct = default)
    {
        SavedTokens.Add((id, accessTokenCiphertext, refreshTokenCiphertext, expiresAt, chatgptAccountId));
        _tokens = new OAuthTokenSet(accessTokenCiphertext, refreshTokenCiphertext, expiresAt, chatgptAccountId);
        return Task.CompletedTask;
    }

    // ── Unused members ────────────────────────────────────────────────
    public Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateAsync(string name, string baseUrl, string? apiKeyPlaintext, string apiKeyHint, int authType, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CloudProviderListItem>>([]);
    public Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<CloudProviderReadItem?>(null);
    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> NameExistsAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task SaveModelsAsync(string id, IReadOnlyList<CloudProviderModelMeta> models, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<CloudProviderModelMeta>> GetModelMetasAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CloudProviderModelMeta>>([]);
}
