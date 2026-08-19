using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Host-path inference proxy tests: a benchmark on a REGISTERED container must hit
/// that container (via RegisteredContainerId label OR image/display-name fallback).
/// </summary>
public sealed class InferenceProxyHostTests
{
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeDockerController _host = new();
    private readonly FakeHealthChecker _healthChecker = new();

    private InferenceProxy CreateProxy() => new(
        _host,
        _healthChecker,
        new LoggerFactory().CreateLogger<InferenceProxy>(),
        _containerRegistry);

    private async Task<(string RegId, string ModelId)> SeedHostRegisteredContainer(
        string image = "vllm-serve",
        string displayName = "vllm-serve")
    {
        var reg = new RegisteredContainer
        {
            Id = "reg-host-1",
            DisplayName = displayName,
            Image = image,
            Agent = "host",
            Status = ContainerRegistrationStatus.Ready,
            MappedPort = 8080,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _containerRegistry.CreateAsync(reg);

        var model = new ModelDefinition
        {
            Id = "llama-3-8b",
            Name = "llama-3-8b",
            Status = ModelStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _containerRegistry.AddModelMappingAsync(reg.Id, model.Id);
        return (reg.Id, model.Id);
    }

    private static InferenceRequest MakeRequest(string modelName)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = modelName,
            OriginalJson = """{"model":"llama-3-8b","messages":[{"role":"user","content":"hello"}],"max_tokens":16}""",
            IsStreaming = false,
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously),
            TargetId = ExecutionTarget.HostId
        };

    /// <summary>
    /// G1: a host benchmark on a REGISTERED container must hit that container.
    /// The fake's ListedContainers carries the RegisteredContainerId label (as the
    /// real DockerController.ListContainersAsync would from the unswarm.registry label).
    /// </summary>
    [Fact]
    public async Task InvokeAsync_HostRegisteredContainer_MatchesByRegisteredContainerId()
    {
        var (regId, _) = await SeedHostRegisteredContainer();

        // Point the proxy at a local TCP listener so the HTTP hop succeeds; we only
        // assert the container was selected (health check ran on its port).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _host.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "host-c1",
                ModelId = "vllm-serve",
                ModelName = "vllm-serve",
                Status = ContainerStatus.Running,
                Port = port,
                RegisteredContainerId = regId
            }
        ];

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            // Consume and respond minimally so the proxy returns a response.
            var stream = client.GetStream();
            var buf = new byte[4096];
            _ = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
            var body = """{"id":"host-ok","choices":[],"usage":{"completion_tokens":3}}""";
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes);
        });

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("llama-3-8b"));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(port, _healthChecker.CheckedPorts);
    }

    /// <summary>
    /// G1 fallback: when the host container lacks the registry label, the proxy must
    /// match by the registered container's image/display-name (docker ps container name).
    /// </summary>
    [Fact]
    public async Task InvokeAsync_HostRegisteredContainer_NoLabel_FallsBackToImageName()
    {
        await SeedHostRegisteredContainer(image: "vllm-serve", displayName: "vllm-serve");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // No RegisteredContainerId — label missing (e.g. pre-registration container).
        _host.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "host-c9",
                ModelId = "vllm-serve",
                ModelName = "vllm-serve",
                Status = ContainerStatus.Running,
                Port = port
            }
        ];

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buf = new byte[4096];
            _ = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
            var body = """{"id":"host-ok2","choices":[],"usage":{"completion_tokens":5}}""";
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes);
        });

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("llama-3-8b"));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(port, _healthChecker.CheckedPorts);
    }
}
