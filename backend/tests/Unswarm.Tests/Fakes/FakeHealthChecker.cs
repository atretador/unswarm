using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

public sealed class FakeHealthChecker : IHealthChecker
{
    public bool IsReady { get; set; } = true;
    public Func<int, CancellationToken, Task<bool>>? CheckFunc { get; set; }
    public List<int> CheckedPorts { get; } = [];

    public Task WaitForReadyAsync(int port, int timeoutSeconds = 120, CancellationToken ct = default)
    {
        CheckedPorts.Add(port);
        if (!IsReady)
            throw new TimeoutException($"Health check timeout on port {port}");
        return Task.CompletedTask;
    }

    public Task WaitForReadyAsync(int port, string host, int timeoutSeconds = 120, CancellationToken ct = default)
    {
        CheckedPorts.Add(port);
        if (!IsReady)
            throw new TimeoutException($"Health check timeout on port {port} host {host}");
        return Task.CompletedTask;
    }

    public Task<bool> CheckAsync(int port, CancellationToken ct = default)
    {
        CheckedPorts.Add(port);
        return CheckFunc != null ? CheckFunc(port, ct) : Task.FromResult(IsReady);
    }

    public Task<bool> CheckAsync(int port, string host, CancellationToken ct = default)
    {
        CheckedPorts.Add(port);
        return CheckFunc != null ? CheckFunc(port, ct) : Task.FromResult(IsReady);
    }
}
