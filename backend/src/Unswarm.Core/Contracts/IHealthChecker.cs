namespace Unswarm.Core.Contracts;

public interface IHealthChecker
{
    Task WaitForReadyAsync(int port, int timeoutSeconds = 120, CancellationToken ct = default);
    Task WaitForReadyAsync(int port, string host, int timeoutSeconds = 120, CancellationToken ct = default);
    Task<bool> CheckAsync(int port, CancellationToken ct = default);
    Task<bool> CheckAsync(int port, string host, CancellationToken ct = default);
}
