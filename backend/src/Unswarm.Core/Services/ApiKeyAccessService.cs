using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

/// <summary>
/// Per-key model access control for the /v1 inference surface. A key's
/// <see cref="KeyAccess"/> lists allowed cloud providers and exact model ids;
/// both arrays empty means unrestricted. Matching rules:
///  - "cloud/&lt;provider&gt;/&lt;model&gt;" → allowed when &lt;provider&gt; is in
///    Providers OR the full id is in Models.
///  - local model → allowed when the model id is in Models OR its owning
///    registered runtime's display name is in Providers.
/// Unknown keys fail closed. A key whose stored AccessJson is malformed fails
/// closed too (the <see cref="KeyAccess.IsMalformed"/> sentinel denies all).
/// </summary>
public interface IApiKeyAccessService
{
    /// <summary>Parsed access restrictions for a key, or null when unknown.</summary>
    Task<KeyAccess?> GetAccessAsync(string keyId, CancellationToken ct = default);

    /// <summary>True when <paramref name="keyId"/> may request <paramref name="modelName"/>.</summary>
    Task<bool> IsModelAllowedAsync(string keyId, string modelName, CancellationToken ct = default);

    /// <summary>
    /// Filters candidate model ids through the key's access rules, returning only
    /// those the key may request. Unknown keys or malformed AccessJson yield an
    /// empty list (fail closed); unrestricted keys yield every candidate.
    /// </summary>
    Task<IReadOnlyList<string>> FilterModelsAsync(string keyId, IEnumerable<string> modelNames, CancellationToken ct = default);
}

public sealed class ApiKeyAccessService : IApiKeyAccessService
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly IContainerRegistry _containers;
    private readonly IApiKeyStore? _keyStore;
    private readonly ILogger<ApiKeyAccessService>? _logger;

    public ApiKeyAccessService(
        Func<UnswarmDbContext> dbFactory,
        IContainerRegistry containers,
        ILogger<ApiKeyAccessService>? logger = null,
        IApiKeyStore? keyStore = null)
    {
        _dbFactory = dbFactory;
        _containers = containers;
        _logger = logger;
        _keyStore = keyStore;
    }

    public async Task<KeyAccess?> GetAccessAsync(string keyId, CancellationToken ct = default)
    {
        // Prefer the store's hot-path cache (invalidated by SaveAccessAsync, 5-min
        // TTL fallback); fall back to a direct read when no store is supplied.
        if (_keyStore is not null)
            return await _keyStore.GetAccessCachedAsync(keyId, ct).ConfigureAwait(false);

        await using var db = _dbFactory();
        var json = await db.ApiKeys
            .Where(k => k.Id == keyId)
            .Select(k => k.AccessJson)
            .FirstOrDefaultAsync(ct);
        return json is null ? null : ApiKeyStore.DeserializeAccess(json);
    }

    public async Task<bool> IsModelAllowedAsync(string keyId, string modelName, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(keyId, ct).ConfigureAwait(false);
        if (access is null)
            return false; // unknown/revoked key — fail closed

        return await IsAllowedCoreAsync(access, modelName, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> FilterModelsAsync(string keyId, IEnumerable<string> modelNames, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(keyId, ct).ConfigureAwait(false);
        if (access is null)
            return []; // unknown/revoked key — fail closed

        if (access.IsMalformed)
        {
            _logger?.LogError(
                "API key {KeyId} has malformed AccessJson; denying every model (fail closed).",
                keyId);
            return [];
        }

        // Unrestricted: both allow-lists empty.
        if (access.Providers.Count == 0 && access.Models.Count == 0)
            return modelNames.ToList();

        var allowed = new List<string>();
        foreach (var modelName in modelNames)
        {
            if (await IsAllowedCoreAsync(access, modelName, ct).ConfigureAwait(false))
                allowed.Add(modelName);
        }

        return allowed;
    }

    /// <summary>Shared matching rules for one already-loaded <see cref="KeyAccess"/>.</summary>
    private async Task<bool> IsAllowedCoreAsync(KeyAccess access, string modelName, CancellationToken ct)
    {
        // Malformed stored JSON must never fall through to "empty lists =
        // unrestricted" — deny everything instead.
        if (access.IsMalformed)
        {
            _logger?.LogError(
                "API key has malformed AccessJson; denying request for model {Model} (fail closed).",
                modelName);
            return false;
        }

        // Unrestricted: both allow-lists empty.
        if (access.Providers.Count == 0 && access.Models.Count == 0)
            return true;

        // Cloud model: "cloud/<provider>/<model>".
        if (modelName.StartsWith("cloud/", StringComparison.Ordinal))
        {
            var provider = ExtractCloudProviderName(modelName);
            return (provider is not null && Contains(access.Providers, provider))
                || Contains(access.Models, modelName);
        }

        // Router model: "router/<profileName>".
        if (modelName.StartsWith("router/", StringComparison.Ordinal))
        {
            var profileName = ExtractRouterProfileName(modelName);
            return (profileName is not null && Contains(access.Providers, profileName))
                || Contains(access.Models, modelName);
        }

        // Local model: exact match first...
        if (Contains(access.Models, modelName))
            return true;

        // ...then by owning runtime display name (the "provider" of a local model).
        var runtimeId = await _containers.GetContainerIdForModelAsync(modelName, ct).ConfigureAwait(false);
        if (runtimeId is null)
            return false;

        var runtime = await _containers.GetAsync(runtimeId, ct).ConfigureAwait(false);
        return runtime is not null && Contains(access.Providers, runtime.DisplayName);
    }

    private static bool Contains(IReadOnlyList<string> list, string value) =>
        list.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Second segment of "cloud/&lt;provider&gt;/&lt;model&gt;", or null.</summary>
    private static string? ExtractCloudProviderName(string modelName)
    {
        var rest = modelName["cloud/".Length..];
        var slashIdx = rest.IndexOf('/');
        return slashIdx > 0 ? rest[..slashIdx] : null;
    }

    /// <summary>Profile name from "router/&lt;profileName&gt;", or null.</summary>
    private static string? ExtractRouterProfileName(string modelName)
    {
        var rest = modelName["router/".Length..];
        return rest.Length > 0 ? rest : null;
    }
}
