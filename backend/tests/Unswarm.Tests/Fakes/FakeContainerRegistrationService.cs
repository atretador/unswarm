using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Minimal IContainerRegistrationService for controller tests. RegisterAsync and
/// RediscoverAsync return a scripted result; DeleteAsync is a no-op.
/// </summary>
public sealed class FakeContainerRegistrationService : IContainerRegistrationService
{
    public RegisteredContainerWithModels DefaultResult { get; set; } = new()
    {
        Container = new RegisteredContainer
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
    public RegisteredContainerWithModels? StartResult { get; set; }

    /// <summary>When set, StartAsync throws this exception instead of returning.</summary>
    public Exception? StartException { get; set; }

    public List<string> StartedIds { get; } = [];

    public Task<RegisteredContainerWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
        => Task.FromResult(DefaultResult);

    public Task<RegisteredContainerWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default)
    {
        StartedIds.Add(registeredContainerId);
        if (StartException is not null)
            throw StartException;
        return Task.FromResult(StartResult ?? DefaultResult);
    }

    public Task<RegisteredContainerWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
        => Task.FromResult(DefaultResult);

    public Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default)
    {
        DeletedIds.Add(id);
        return Task.CompletedTask;
    }
}
