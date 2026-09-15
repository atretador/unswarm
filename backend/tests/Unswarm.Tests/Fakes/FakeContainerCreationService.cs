using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

public sealed class FakeContainerCreationService : IContainerCreationService
{
    public Func<CreateContainerRequest, CancellationToken, Task<CreateContainerResult>>? OnCreate { get; set; }

    public Task<CreateContainerResult> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default)
    {
        if (OnCreate is not null) return OnCreate(request, ct);

        return Task.FromResult(new CreateContainerResult(
            "fake-runtime-1",
            ContainerCreationStatus.Running,
            "fake-container-1",
            "created",
            null));
    }

    public Task<CreateContainerResult> GetCreateStatusAsync(string runtimeId, CancellationToken ct = default)
        => Task.FromResult(new CreateContainerResult(runtimeId, ContainerCreationStatus.Running, "fake-container-1", "ok", null));
}
