using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Unswarm.Core.Services;

public sealed class HealthChecker : Contracts.IHealthChecker
{
    private readonly ILogger<HealthChecker> _logger;

    public HealthChecker(ILogger<HealthChecker> logger)
    {
        _logger = logger;
    }

    public async Task WaitForReadyAsync(int port, int timeoutSeconds = 120, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await CheckAsync(port, ct).ConfigureAwait(false)) return;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Health check timed out on port {port} after {timeoutSeconds}s — container may still be loading the model");
    }

    public async Task<bool> CheckAsync(int port, CancellationToken ct = default)
    {
        // TCP check
        if (!await TcpConnectAsync(port, ct).ConfigureAwait(false))
            return false;

        // HTTP /health
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"http://127.0.0.1:{port}/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TcpConnectAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
