using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

public sealed class FakeModelTargetResolver : IModelTargetResolver
{
    public Func<string, CancellationToken, Task<string>>? ResolveFunc { get; set; }

    public Task<string> ResolveTargetAsync(string modelName, CancellationToken ct = default)
        => ResolveFunc is not null
            ? ResolveFunc(modelName, ct)
            : Task.FromResult("host");
}
