namespace Unswarm.Core.Contracts;

public interface IHealthChecker
{
    Task WaitForReadyAsync(int port, CancellationToken ct = default);
    Task<bool> CheckAsync(int port, CancellationToken ct = default);
}
