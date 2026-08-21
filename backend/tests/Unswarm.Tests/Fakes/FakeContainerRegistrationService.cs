using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Minimal IContainerRegistrationService for controller tests. RegisterAsync and
/// RediscoverAsync return a scripted result; DeleteAsync is a no-op.
/// </summary>
public sealed class FakeContainerRegistrationService : IContainerRegistrationService
{
    public RegisteredRuntimeWithModels DefaultResult { get; set; } = new()
    {
        Container = new RegisteredRuntime
        {
            Id = "reg-default",
            DisplayName = "default",
            Image = "default:latest",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        },
        DiscoveredModels = []
    };

    public List<string> DeletedIds { get; } = [];

    /// <summary>Scriptable StartAsync result; when set, returned for any id.</summary>
    public RegisteredRuntimeWithModels? StartResult { get; set; }

    /// <summary>When set, StartAsync throws this exception instead of returning.</summary>
    public Exception? StartException { get; set; }

    public List<string> StartedIds { get; } = [];

    public Task<RegisteredRuntimeWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
        => Task.FromResult(DefaultResult);

    public Task<RegisteredRuntimeWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default)
    {
        StartedIds.Add(registeredContainerId);
        if (StartException is not null)
            throw StartException;
        return Task.FromResult(StartResult ?? DefaultResult);
    }

    public Task<RegisteredRuntimeWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
        => Task.FromResult(DefaultResult);

    public Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default)
    {
        DeletedIds.Add(id);
        return Task.CompletedTask;
    }

    public List<string> UpdatedConcurrencyIds { get; } = [];

    /// <summary>The last canRunAlongWith list received by UpdateCanRunAlongWithAsync.</summary>
    public List<string>? LastConcurrencyList { get; set; }

    /// <summary>Scriptable UpdateCanRunAlongWithAsync result; when set, returned for any id.</summary>
    public RegisteredRuntime? UpdateConcurrencyResult { get; set; }

    /// <summary>When set, UpdateCanRunAlongWithAsync returns null (simulates unknown id).</summary>
    public bool UpdateConcurrencyReturnsNull { get; set; }

    public Task<RegisteredRuntime?> UpdateCanRunAlongWithAsync(string id, IReadOnlyList<string> canRunAlongWith, CancellationToken ct = default)
    {
        UpdatedConcurrencyIds.Add(id);
        LastConcurrencyList = canRunAlongWith.ToList();
        if (UpdateConcurrencyReturnsNull)
            return Task.FromResult<RegisteredRuntime?>(null);
        return Task.FromResult<RegisteredRuntime?>(UpdateConcurrencyResult);
    }

    // ── StopAsync ──────────────────────────────────────────────────────

    public List<string> StoppedIds { get; } = [];

    /// <summary>Scriptable StopAsync result; when set, returned for any id.</summary>
    public RegisteredRuntime? StopResult { get; set; }

    /// <summary>When set, StopAsync throws this exception instead of returning.</summary>
    public Exception? StopException { get; set; }

    public Task<RegisteredRuntime?> StopAsync(string id, CancellationToken ct = default)
    {
        StoppedIds.Add(id);
        if (StopException is not null)
            throw StopException;
        return Task.FromResult(StopResult);
    }

    // ── ResolveLiveContainerIdAsync ────────────────────────────────────

    public List<string> ResolvedLiveIds { get; } = [];

    /// <summary>
    /// Scriptable resolver: maps a requested (possibly stale) container id to the id
    /// that should be returned. Unmapped ids pass through unchanged.
    /// </summary>
    public Dictionary<string, string> LiveIdMap { get; } = new();

    public Task<string> ResolveLiveContainerIdAsync(string runtimeContainerId, CancellationToken ct = default)
    {
        ResolvedLiveIds.Add(runtimeContainerId);
        return Task.FromResult(LiveIdMap.GetValueOrDefault(runtimeContainerId, runtimeContainerId));
    }
}
