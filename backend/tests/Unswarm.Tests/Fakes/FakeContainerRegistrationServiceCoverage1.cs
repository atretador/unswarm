using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IContainerRegistrationService"/> for coverage tests.
/// Every method is configurable via a result and/or an exception so controller
/// error branches (404/400/500) can be exercised without Docker.
/// </summary>
public sealed class FakeContainerRegistrationServiceCoverage1 : IContainerRegistrationService
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

    public RegisteredRuntimeWithModels? RegisterResult { get; set; }
    public Exception? RegisterException { get; set; }

    public RegisteredRuntimeWithModels? StartResult { get; set; }
    public Exception? StartException { get; set; }

    public RegisteredRuntimeWithModels? RediscoverResult { get; set; }
    public Exception? RediscoverException { get; set; }

    public Exception? DeleteException { get; set; }
    public List<string> DeletedIds { get; } = [];

    public RegisteredRuntime? UpdateConcurrencyResult { get; set; }

    public (RegisteredRuntime A, RegisteredRuntime B)? ToggleConcurrencyResult { get; set; }

    public RegisteredRuntime? StopResult { get; set; }

    public RegisteredRuntime? HealthCheckResult { get; set; }
    public Exception? HealthCheckException { get; set; }

    public Dictionary<string, string> LiveIdMap { get; } = new();

    public Task<RegisteredRuntimeWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
    {
        if (RegisterException is not null) throw RegisterException;
        return Task.FromResult(RegisterResult ?? DefaultResult);
    }

    public Task<RegisteredRuntimeWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default)
    {
        if (StartException is not null) throw StartException;
        return Task.FromResult(StartResult ?? DefaultResult);
    }

    public Task<RegisteredRuntimeWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
    {
        if (RediscoverException is not null) throw RediscoverException;
        return Task.FromResult(RediscoverResult ?? DefaultResult);
    }

    public Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default)
    {
        if (DeleteException is not null) throw DeleteException;
        DeletedIds.Add(id);
        return Task.CompletedTask;
    }

    public Task<RegisteredRuntime?> UpdateCanRunAlongWithAsync(string id, IReadOnlyList<string> canRunAlongWith, CancellationToken ct = default)
        => Task.FromResult(UpdateConcurrencyResult);

    public Task<(RegisteredRuntime A, RegisteredRuntime B)?> ToggleConcurrencyAsync(string runtimeAId, string runtimeBId, bool canRunAlongWith, CancellationToken ct = default)
        => Task.FromResult(ToggleConcurrencyResult);

    public Task<RegisteredRuntime?> StopAsync(string id, CancellationToken ct = default)
        => Task.FromResult(StopResult);

    public Task<RegisteredRuntime?> HealthCheckAsync(string id, CancellationToken ct = default)
    {
        if (HealthCheckException is not null) throw HealthCheckException;
        return Task.FromResult(HealthCheckResult);
    }

    public Task<string> ResolveLiveContainerIdAsync(string runtimeContainerId, CancellationToken ct = default)
        => Task.FromResult(LiveIdMap.GetValueOrDefault(runtimeContainerId, runtimeContainerId));
}
