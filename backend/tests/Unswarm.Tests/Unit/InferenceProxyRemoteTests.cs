using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class InferenceProxyRemoteTests
{
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeRemoteDockerController _remote = new();
    private readonly FakeDockerController _host = new();
    private readonly FakeDockerControllerRouter _router;
    private readonly FakeHealthChecker _healthChecker = new();

    public InferenceProxyRemoteTests()
    {
        _router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController>
            {
                ["host"] = _host,
                ["agent:gpu1"] = _remote
            });
    }

    private InferenceProxy CreateProxy()
        => new(
            _host,
            _healthChecker,
            new LoggerFactory().CreateLogger<InferenceProxy>(),
            NullServiceProvider.Instance,
            Options.Create(new ContainerHostOptions()),
            _containerRegistry,
            _router);

    private async Task<(string RegId, string ModelId)> SeedRemoteModel()
    {
        var reg = new RegisteredRuntime
        {
            Id = "reg-remote-1",
            DisplayName = "vllm-serve",
            Image = "vllm-serve",
            Agent = "gpu1",
            Status = ContainerRegistrationStatus.Ready,
            MappedPort = 9090,
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

        _remote.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "remote-c1",
                ModelId = "vllm-serve",
                ModelName = "vllm-serve",
                Status = ContainerStatus.Running,
                Port = 9090,
                RegisteredRuntimeId = reg.Id
            }
        ];

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
            TargetId = "agent:gpu1"
        };

    [Fact]
    public async Task InvokeAsync_RemoteTarget_ProxiesViaInferAsync()
    {
        await SeedRemoteModel();
        _remote.InferResult = """{"id":"chatcmpl-r","choices":[{"message":{"role":"assistant","content":"hi"}}],"usage":{"prompt_tokens":4,"completion_tokens":7,"total_tokens":11}}""";

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("llama-3-8b"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal(7, response.TokensGenerated);

        // Body carries the raw response
        using var reader = new StreamReader(response.Body!);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("chatcmpl-r", body);

        // The request JSON was forwarded verbatim
        var inferCall = Assert.Single(_remote.InferCalls);
        Assert.Equal(9090, inferCall.Port);
        Assert.Contains("\"max_tokens\":16", inferCall.RequestJson);
    }

    [Fact]
    public async Task InvokeAsync_RemoteTarget_NoRunningContainer_Returns503()
    {
        await SeedRemoteModel();
        _remote.ListedContainers = [];

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("llama-3-8b"));

        Assert.Equal(503, response.StatusCode);
        Assert.Empty(_remote.InferCalls);
    }

    [Fact]
    public async Task InvokeAsync_RemoteTarget_InferFailure_Returns502()
    {
        await SeedRemoteModel();
        _remote.InferFunc = (port, body, ct) => throw new InvalidOperationException("agent lost");

        var proxy = CreateProxy();
        // Shrink the warmup-retry hold window: production (180s) would retry the
        // persistent failure for minutes before surfacing the 502.
        proxy.HoldSecondsOverride = 1;

        var response = await proxy.InvokeAsync(MakeRequest("llama-3-8b"));

        Assert.Equal(502, response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_RemoteTarget_TokenParseAbsent_ReturnsZero()
    {
        await SeedRemoteModel();
        _remote.InferResult = """{"id":"x","choices":[{"message":{"content":"hi"}}]}""";

        var proxy = CreateProxy();
        var response = await proxy.InvokeAsync(MakeRequest("llama-3-8b"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(0, response.TokensGenerated);
    }
}
