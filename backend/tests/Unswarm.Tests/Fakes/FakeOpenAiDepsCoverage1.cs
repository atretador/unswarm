using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Fakes;

/// <summary>Fake ChatGPT-subscription forwarding service for OpenAIController tests.</summary>
public sealed class FakeChatGptSubscriptionCoverage1 : IChatGPTSubscriptionForwardingService
{
    public List<(string ModelId, string RequestBody, string RequestPath, bool IsStreaming)> Forwarded { get; } = [];

    /// <summary>Body bytes returned by <see cref="ForwardAsync"/>.</summary>
    public byte[] Body { get; set; } = "data: {}\n\n"u8.ToArray();

    public Task<Stream> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct,
        Dictionary<string, string>? forwardedHeaders = null)
    {
        Forwarded.Add((modelId, requestBody, requestPath, isStreaming));
        return Task.FromResult<Stream>(new MemoryStream(Body));
    }
}

/// <summary>Scriptable <see cref="IApiKeyAccessService"/> for OpenAIController tests.</summary>
public sealed class FakeApiKeyAccessCoverage1 : IApiKeyAccessService
{
    public bool Allowed { get; set; } = true;

    public Func<string, string, bool>? AllowedFunc { get; set; }

    public Func<string, IEnumerable<string>, IReadOnlyList<string>>? FilterFunc { get; set; }

    public Task<KeyAccess?> GetAccessAsync(string keyId, CancellationToken ct = default)
        => Task.FromResult<KeyAccess?>(null);

    public Task<bool> IsModelAllowedAsync(string keyId, string modelName, CancellationToken ct = default)
        => Task.FromResult(AllowedFunc?.Invoke(keyId, modelName) ?? Allowed);

    public Task<IReadOnlyList<string>> FilterModelsAsync(string keyId, IEnumerable<string> modelNames, CancellationToken ct = default)
        => Task.FromResult(FilterFunc?.Invoke(keyId, modelNames) ?? modelNames.ToList());
}

/// <summary>
/// <see cref="ICloudForwardingService"/> that throws the configured exception,
/// for exercising the OpenAIController cloud error branches.
/// </summary>
public sealed class FakeThrowingCloudForwardingCoverage1 : ICloudForwardingService
{
    public Exception? Exception { get; set; }

    public Task<CloudForwardResponse> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct,
        Dictionary<string, string>? forwardedHeaders = null)
        => throw (Exception ?? new InvalidOperationException("boom"));
}

/// <summary>Scriptable <see cref="IRouterProfileService"/> for OpenAIController tests.</summary>
public sealed class FakeRouterProfileServiceCoverage1 : IRouterProfileService
{
    public List<RouterProfile> Profiles { get; } = [];

    public List<(string ProfileName, string? ActiveModelId)> SetActiveCalls { get; } = [];

    public Func<string, IReadOnlyList<RouterProfileEntry>?>? ResolveEntriesFunc { get; set; }

    public Func<string, RouterProfileMode?>? GetModeFunc { get; set; }

    public Func<string, (IReadOnlyList<RouterProfileEntry> Entries, RouterProfileMode Mode)?>? ResolveFunc { get; set; }

    public Task<IReadOnlyList<RouterProfileEntry>?> ResolveEntriesAsync(string profileName, CancellationToken ct = default)
        => Task.FromResult(ResolveEntriesFunc?.Invoke(profileName));

    public Task<RouterProfileMode?> GetModeAsync(string profileName, CancellationToken ct = default)
        => Task.FromResult(GetModeFunc?.Invoke(profileName));

    public Task<(IReadOnlyList<RouterProfileEntry> Entries, RouterProfileMode Mode)?> ResolveAsync(string profileName, CancellationToken ct = default)
        => Task.FromResult(ResolveFunc?.Invoke(profileName));

    public Task<IReadOnlyList<RouterProfile>> ListProfilesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RouterProfile>>(Profiles.ToList());

    public Task SetActiveModelIdAsync(string profileName, string? activeModelId, CancellationToken ct = default)
    {
        SetActiveCalls.Add((profileName, activeModelId));
        return Task.CompletedTask;
    }

    public Task<RouterProfile> SetThinkingEffortAsync(string profileName, string modelId, string? thinkingEffortOverride, CancellationToken ct = default)
        => throw new NotSupportedException();
}
