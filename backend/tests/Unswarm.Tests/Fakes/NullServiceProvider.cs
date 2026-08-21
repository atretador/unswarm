using Microsoft.Extensions.DependencyInjection;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Minimal IServiceProvider + IServiceScopeFactory for unit tests.
/// Returns null for most types; provides a working scope for IContainerRegistrationService.
/// </summary>
public sealed class NullServiceProvider : IServiceProvider, IServiceScopeFactory
{
    public static readonly NullServiceProvider Instance = new();

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceScopeFactory))
            return this;
        if (serviceType == typeof(IContainerRegistrationService))
            return new NullContainerRegistrationService();
        return null;
    }

    public IServiceScope CreateScope() => new NullScope();

    private sealed class NullScope : IServiceScope
    {
        public IServiceProvider ServiceProvider => Instance;
        public void Dispose() { }
    }
}

/// <summary>
/// Stub IContainerRegistrationService that returns a failure for StartAsync.
/// </summary>
internal sealed class NullContainerRegistrationService : IContainerRegistrationService
{
    public Task<RegisteredRuntimeWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("Null service");

    public Task<RegisteredRuntimeWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default)
        => Task.FromResult(new RegisteredRuntimeWithModels
        {
            Container = new RegisteredRuntime
            {
                Id = registeredContainerId,
                Image = registeredContainerId,
                Status = ContainerRegistrationStatus.Error,
                ErrorMessage = "NullContainerRegistrationService"
            },
            DiscoveredModels = []
        });

    public Task<RegisteredRuntimeWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
        => throw new NotSupportedException("Null service");

    public Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default)
        => throw new NotSupportedException("Null service");

    public Task<RegisteredRuntime?> UpdateCanRunAlongWithAsync(string id, IReadOnlyList<string> canRunAlongWith, CancellationToken ct = default)
        => throw new NotSupportedException("Null service");
}
