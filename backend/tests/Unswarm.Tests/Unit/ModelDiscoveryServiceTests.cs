using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class ModelDiscoveryServiceTests : IDisposable
{
    private readonly ILogger<ModelDiscoveryService> _logger =
        new LoggerFactory().CreateLogger<ModelDiscoveryService>();
    private readonly List<TcpListener> _listeners = [];

    [Fact]
    public async Task DiscoverModelsAsync_EmptyData_ReturnsEmptyList()
    {
        var port = StartServer("""{"data":[]}""");
        var service = new ModelDiscoveryService(_logger);

        var models = await service.DiscoverModelsAsync(port);

        Assert.Empty(models);
    }

    [Fact]
    public async Task DiscoverModelsAsync_SingleModel_ReturnsOne()
    {
        var port = StartServer("""{"data":[{"id":"llama3","owned_by":"ollama"}]}""");
        var service = new ModelDiscoveryService(_logger);

        var models = await service.DiscoverModelsAsync(port);

        Assert.Single(models);
        Assert.Equal("llama3", models[0].ModelId);
        Assert.Equal("ollama", models[0].OwnedBy);
    }

    [Fact]
    public async Task DiscoverModelsAsync_MultipleModels_ReturnsAll()
    {
        var json = """{"data":[{"id":"llama3","owned_by":"ollama"},{"id":"mistral","owned_by":"mistralai"},{"id":"codellama","owned_by":"meta"}]}""";
        var port = StartServer(json);
        var service = new ModelDiscoveryService(_logger);

        var models = await service.DiscoverModelsAsync(port);

        Assert.Equal(3, models.Count);
        Assert.Equal("llama3", models[0].ModelId);
        Assert.Equal("mistral", models[1].ModelId);
        Assert.Equal("codellama", models[2].ModelId);
    }

    [Fact]
    public async Task DiscoverModelsAsync_MissingId_SkipsEntry()
    {
        var json = """{"data":[{"owned_by":"ollama"},{"id":"valid-model","owned_by":"ollama"}]}""";
        var port = StartServer(json);
        var service = new ModelDiscoveryService(_logger);

        var models = await service.DiscoverModelsAsync(port);

        Assert.Single(models);
        Assert.Equal("valid-model", models[0].ModelId);
    }

    [Fact]
    public async Task DiscoverModelsAsync_NoDataProperty_ReturnsEmpty()
    {
        var port = StartServer("""{"models":[]}""");
        var service = new ModelDiscoveryService(_logger);

        var models = await service.DiscoverModelsAsync(port);

        Assert.Empty(models);
    }

    [Fact]
    public async Task DiscoverModelsAsync_MalformedJson_ReturnsEmpty()
    {
        var port = StartServer("not json at all");
        var service = new ModelDiscoveryService(_logger);

        var models = await service.DiscoverModelsAsync(port);

        Assert.Empty(models);
    }

    [Fact]
    public async Task DiscoverModelsAsync_HttpError_Throws()
    {
        var port = StartServerError();
        var service = new ModelDiscoveryService(_logger);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.DiscoverModelsAsync(port));
    }

    [Fact]
    public async Task DiscoverModelsAsync_ConnectionRefused_Throws()
    {
        // Port almost certainly not listening → transport failure must surface,
        // not silently return an empty list.
        var service = new ModelDiscoveryService(_logger);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.DiscoverModelsAsync(1));
    }

    private int StartServer(string jsonResponse)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);
                    using var writer = new StreamWriter(stream) { AutoFlush = true };

                    // Read request line and headers
                    await reader.ReadLineAsync();
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && line.Length > 0) { }

                    var bodyBytes = Encoding.UTF8.GetBytes(jsonResponse);
                    await writer.WriteAsync(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\n\r\n");
                    await stream.WriteAsync(bodyBytes);
                }
            }
            catch { /* listener stopped */ }
        });

        return port;
    }

    private int StartServerError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);
                    using var writer = new StreamWriter(stream) { AutoFlush = true };

                    await reader.ReadLineAsync();
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && line.Length > 0) { }

                    await writer.WriteAsync("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n");
                }
            }
            catch { /* listener stopped */ }
        });

        return port;
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
    }
}
