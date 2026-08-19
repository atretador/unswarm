using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Services.Validation;

namespace Unswarm.Tests.Unit;

public sealed class ModelValidatorTests : IDisposable
{
    private readonly ILogger<ModelValidator> _logger = new LoggerFactory().CreateLogger<ModelValidator>();

    [Fact]
    public async Task ValidateAsync_FailsWhenPortNotListening()
    {
        var validator = new ModelValidator(_logger);

        // Port 1 is almost certainly not listening
        var result = await validator.ValidateAsync(1, "test-model");

        Assert.False(result.IsSuccess);
        Assert.Contains("TCP", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_FailsWhenHealthNotResponding()
    {
        // Start a TCP listener (accepts connections) but doesn't serve HTTP
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;

        var validator = new ModelValidator(_logger);
        var result = await validator.ValidateAsync(port, "test-model");

        // TCP succeeds, but /health fails
        Assert.False(result.IsSuccess);

        tcpListener.Stop();
    }

    [Fact]
    public async Task ValidateAsync_SuccessWithFullServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var port = GetAvailablePort();
        var server = new TestModelServer(port, "test-model");
        server.Start();

        try
        {
            var validator = new ModelValidator(_logger);
            var result = await validator.ValidateAsync(port, "test-model", cts.Token);

            Assert.True(result.IsSuccess);
            Assert.Null(result.ErrorMessage);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ValidateAsync_FailsIdentityCheck_WrongModelName()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var port = GetAvailablePort();
        var server = new TestModelServer(port, "wrong-model");
        server.Start();

        try
        {
            var validator = new ModelValidator(_logger);
            var result = await validator.ValidateAsync(port, "expected-model", cts.Token);

            Assert.False(result.IsSuccess);
            Assert.Contains("identity", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ModelValidationResult_Success_Factory()
    {
        var result = ModelValidationResult.Success();
        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ModelValidationResult_Fail_Factory()
    {
        var result = ModelValidationResult.Fail("something broke");
        Assert.False(result.IsSuccess);
        Assert.Equal("something broke", result.ErrorMessage);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() { }

    /// <summary>
    /// Minimal HTTP server that handles /health, /v1/models, /v1/chat/completions.
    /// </summary>
    private sealed class TestModelServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _modelName;
        private Task? _runTask;

        public TestModelServer(int port, string modelName)
        {
            _modelName = modelName;
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        public void Start()
        {
            _listener.Start();
            _runTask = RunAsync();
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync().WaitAsync(_cts.Token);
                    _ = HandleClientAsync(client);
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };

                var requestLine = await reader.ReadLineAsync() ?? "";
                var parts = requestLine.Split(' ');
                var path = parts.Length >= 2 ? parts[1] : "/";

                // Consume headers
                string? line;
                while ((line = await reader.ReadLineAsync()) != null && line.Length > 0) { }

                string body;
                if (path == "/health")
                {
                    body = "OK";
                    await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}");
                }
                else if (path == "/v1/models")
                {
                    body = $$"""{"data":[{"id":"{{_modelName}}"}]}""";
                    await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
                }
                else if (path == "/v1/chat/completions")
                {
                    body = """{"choices":[{"message":{"content":"hi"}}]}""";
                    await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
                }
                else
                {
                    await writer.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                }
            }
            catch { /* client disconnected */ }
            finally { client.Dispose(); }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
