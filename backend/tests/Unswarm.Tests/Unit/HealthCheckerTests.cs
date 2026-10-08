using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Models;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for the real <see cref="HealthChecker"/> against a loopback TCP server
/// (no external network / Docker). Covers the TCP probe, the HTTP /health
/// success/failure branches, the default-host overloads, and the polling
/// WaitForReadyAsync success + timeout paths.
/// </summary>
public sealed class HealthCheckerTests
{
    private static HealthChecker CreateChecker(string host = "127.0.0.1")
        => new(new LoggerFactory().CreateLogger<HealthChecker>(),
            Options.Create(new ContainerHostOptions { Host = host }));

    [Fact]
    public async Task CheckAsync_HealthyEndpoint_ReturnsTrue()
    {
        using var server = new LoopbackHttpServer();
        var checker = CreateChecker();

        Assert.True(await checker.CheckAsync(server.Port));
        Assert.True(await checker.CheckAsync(server.Port, "127.0.0.1"));
    }

    [Fact]
    public async Task CheckAsync_HttpErrorStatus_ReturnsFalse()
    {
        using var server = new LoopbackHttpServer
        {
            Response = "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        };
        var checker = CreateChecker();

        Assert.False(await checker.CheckAsync(server.Port));
    }

    [Fact]
    public async Task CheckAsync_NothingListening_ReturnsFalse()
    {
        var port = GetClosedPort();
        var checker = CreateChecker();

        Assert.False(await checker.CheckAsync(port));
    }

    [Fact]
    public async Task CheckAsync_MalformedHttpResponse_ReturnsFalse()
    {
        using var server = new LoopbackHttpServer { Response = "not-a-valid-http-response\r\n\r\n" };
        var checker = CreateChecker();

        Assert.False(await checker.CheckAsync(server.Port));
    }

    [Fact]
    public async Task WaitForReadyAsync_HealthyEndpoint_ReturnsWithoutThrowing()
    {
        using var server = new LoopbackHttpServer();
        var checker = CreateChecker();

        await checker.WaitForReadyAsync(server.Port, timeoutSeconds: 5);
        await checker.WaitForReadyAsync(server.Port, "127.0.0.1", timeoutSeconds: 5);
    }

    [Fact]
    public async Task WaitForReadyAsync_NeverReady_ThrowsTimeout()
    {
        var port = GetClosedPort();
        var checker = CreateChecker();

        await Assert.ThrowsAsync<TimeoutException>(
            () => checker.WaitForReadyAsync(port, timeoutSeconds: 1));
    }

    [Fact]
    public async Task WaitForReadyAsync_AlreadyCancelled_ThrowsOperationCancelled()
    {
        using var server = new LoopbackHttpServer();
        var checker = CreateChecker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checker.WaitForReadyAsync(server.Port, "127.0.0.1", timeoutSeconds: 5, cts.Token));
    }

    private static int GetClosedPort()
    {
        using var server = new LoopbackHttpServer();
        return server.Port; // listener disposed on scope exit
    }

    /// <summary>Minimal loopback HTTP/1.1 server: accepts, drains a request, writes a canned response.</summary>
    private sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public LoopbackHttpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string Response { get; set; } =
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[1024];
                            await Task.WhenAny(
                                stream.ReadAsync(buffer, 0, buffer.Length),
                                Task.Delay(200));
                            var bytes = Encoding.ASCII.GetBytes(Response);
                            await stream.WriteAsync(bytes);
                            await stream.FlushAsync();
                        }
                        catch
                        {
                            // best effort
                        }
                    }
                });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
