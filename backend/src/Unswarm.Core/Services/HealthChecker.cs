using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

public sealed class HealthChecker : Contracts.IHealthChecker
{
    private readonly ILogger<HealthChecker> _logger;
    private readonly string _defaultHost;

    public HealthChecker(ILogger<HealthChecker> logger, IOptions<ContainerHostOptions> options)
    {
        _logger = logger;
        _defaultHost = options.Value.Host;
    }

    public async Task WaitForReadyAsync(int port, int timeoutSeconds = 120, CancellationToken ct = default)
        => await WaitForReadyAsync(port, _defaultHost, timeoutSeconds, ct).ConfigureAwait(false);

    public async Task WaitForReadyAsync(int port, string host, int timeoutSeconds = 120, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await CheckAsync(port, host, ct).ConfigureAwait(false)) return;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Health check timed out on {host}:{port} after {timeoutSeconds}s — container may still be loading the model");
    }

    public async Task<bool> CheckAsync(int port, CancellationToken ct = default)
        => await CheckAsync(port, _defaultHost, ct).ConfigureAwait(false);

    public async Task<bool> CheckAsync(int port, string host, CancellationToken ct = default)
    {
        // TCP check
        if (!await TcpConnectAsync(port, host, ct).ConfigureAwait(false))
            return false;

        // HTTP /health
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"http://{host}:{port}/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TcpConnectAsync(int port, string host, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
