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

    public Task<RegisteredContainerWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
        => Task.FromResult(DefaultResult);

    public Task<RegisteredContainerWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
        => Task.FromResult(DefaultResult);

    public Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default)
    {
        DeletedIds.Add(id);
        return Task.CompletedTask;
    }
}
