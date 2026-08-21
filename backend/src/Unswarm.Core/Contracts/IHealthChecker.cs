namespace Unswarm.Core.Contracts;

public interface IHealthChecker
{
    Task WaitForReadyAsync(int port, int timeoutSeconds = 120, CancellationToken ct = default);
    Task<bool> CheckAsync(int port, CancellationToken ct = default);
}
