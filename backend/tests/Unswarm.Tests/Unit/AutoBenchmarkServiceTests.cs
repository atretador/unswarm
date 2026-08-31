using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Benchmarks;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class AutoBenchmarkServiceTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly FakePromptStore _prompts = new();
    private readonly FakeSchedulerQueue _scheduler = new();
    private readonly FakeBenchmarkHistory _history = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLogStore _logStore = new();
    private readonly ILogger<AutoBenchmarkService> _logger =
        new LoggerFactory().CreateLogger<AutoBenchmarkService>();

    private static readonly ModelDefinition TestModel = new()
    {
        Id = "llama-3-8b",
        Name = "llama-3-8b",
        Status = ModelStatus.Ready,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private AutoBenchmarkService CreateService() => new(
        _settings,
        _prompts,
        _scheduler,
        _history,
        _clock,
        _logStore,
        _logger);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        // Final check so a just-satisfied condition is not missed.
        Assert.True(condition(), "Condition was not met within the timeout");
    }

    // ── Enabled runs ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Enabled_WithStoreDefault_UsesDefaultPromptAndAddsHistory()
    {
        var saved = await _prompts.CreateAsync("My Prompt", "Summarize this text in detail.");
        await _prompts.SetDefaultAsync(saved.Id);

        var service = CreateService();
        await service.RunDefaultBenchmarkAsync(TestModel);

        var entry = Assert.Single(_history.Entries);
        Assert.Equal("llama-3-8b", entry.ModelId);
        Assert.Equal(saved.Text, entry.Prompt);
        Assert.Equal(saved.Id, entry.PromptId);
        Assert.Equal(saved.Name, entry.PromptName);
        Assert.Equal("completed", entry.Status);
        Assert.Null(entry.ErrorMessage);
        // Default fake response generates 42 tokens → tok/s must be positive.
        Assert.True(entry.TokensPerSec > 0);
        Assert.Equal(42, entry.TokensGenerated);

        // Wire format is byte-identical to the controller's BuildChatPayload.
        var request = Assert.Single(_scheduler.EnqueuedRequests);
        Assert.Equal(TestModel.Name, request.ModelName);
        Assert.False(request.IsStreaming);
        Assert.Equal(0, request.Priority);
        Assert.Equal(_clock.UtcNow, request.EnqueuedAt);
        Assert.Equal(
            BenchmarkDefaults.BuildChatPayload(TestModel.Id, saved.Text),
            request.OriginalJson);
    }

    [Fact]
    public async Task RunAsync_Enabled_NoStoreDefault_FallsBackToBuiltInConst()
    {
        var service = CreateService();
        await service.RunDefaultBenchmarkAsync(TestModel);

        var entry = Assert.Single(_history.Entries);
        Assert.Equal(BenchmarkDefaults.DefaultBenchmarkPrompt, entry.Prompt);
        Assert.Null(entry.PromptId);
        Assert.Null(entry.PromptName);
        Assert.Equal("completed", entry.Status);
    }

    [Fact]
    public async Task RunAsync_ServerTokensPerSec_PreferredOverLocalComputation()
    {
        _scheduler.DefaultResponse = new InferenceResponse
        {
            StatusCode = 200,
            TokensGenerated = 10,
            ServerTokensPerSec = 123.5
        };

        var service = CreateService();
        await service.RunDefaultBenchmarkAsync(TestModel);

        var entry = Assert.Single(_history.Entries);
        Assert.Equal(123.5, entry.TokensPerSec);
    }

    // ── Disabled ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Disabled_DoesNotEnqueueOrRecordHistory()
    {
        _settings.UpdateAsync(new Settings { EnableBenchmarking = false }).GetAwaiter().GetResult();

        var service = CreateService();
        await service.RunDefaultBenchmarkAsync(TestModel);

        Assert.Empty(_scheduler.EnqueuedRequests);
        Assert.Empty(_history.Entries);
    }

    // ── Failure containment ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SchedulerThrows_RecordsFailedEntry_AndDoesNotThrow()
    {
        _scheduler.EnqueueFunc = (_, _) => throw new InvalidOperationException("model exploded");

        var service = CreateService();

        var ex = await Record.ExceptionAsync(() => service.RunDefaultBenchmarkAsync(TestModel));
        Assert.Null(ex);

        var entry = Assert.Single(_history.Entries);
        Assert.Equal("error", entry.Status);
        Assert.Equal(0, entry.TokensPerSec);
        Assert.Equal(0, entry.TokensGenerated);
        Assert.Equal("model exploded", entry.ErrorMessage);
        // The prompt that failed is still attributed.
        Assert.Equal(BenchmarkDefaults.DefaultBenchmarkPrompt, entry.Prompt);
    }

    [Fact]
    public async Task RunAsync_TimeoutClampedToAtLeastFiveSeconds()
    {
        // RequestTimeout below the floor must be clamped up to >= 5s. We can't observe
        // the raw CancelAfter delay directly through the fake, but we can assert the
        // run completes and the linked token IS cancellable (timeout armed).
        CancellationToken seenToken = default;
        _scheduler.EnqueueFunc = (_, ct) =>
        {
            seenToken = ct;
            return Task.FromResult(_scheduler.DefaultResponse);
        };

        _settings.UpdateAsync(new Settings { RequestTimeout = 1 }).GetAwaiter().GetResult();

        var service = CreateService();
        await service.RunDefaultBenchmarkAsync(TestModel);

        Assert.True(seenToken.CanBeCanceled, "expected a linked timeout token");
        Assert.Equal("completed", Assert.Single(_history.Entries).Status);
    }

    // ── ContainerRegistrationService integration ──────────────────────────────

    private static FakeRemoteDockerController MakeRemoteWithModels(params string[] modelIds)
    {
        return new FakeRemoteDockerController
        {
            StartResult = new ContainerStartResult { ContainerId = "remote-c1" },
            ListedContainers =
            [
                new ContainerInfo
                {
                    Id = "remote-c1",
                    ModelId = "vllm-serve",
                    ModelName = "vllm-serve",
                    Status = ContainerStatus.Running,
                    Port = 9090
                }
            ],
            Healthy = true,
            Discovered = modelIds.Select(id => new DiscoveredModel { ModelId = id, OwnedBy = "meta" }).ToList()
        };
    }

    private static ContainerRegistrationService MakeRegistrationService(
        FakeContainerRegistry registry,
        FakeDockerControllerRouter router,
        FakeHealthChecker healthChecker,
        FakeModelRegistry modelRegistry,
        AutoBenchmarkService? autoBenchmark)
    {
        return new ContainerRegistrationService(
            registry,
            router,
            healthChecker,
            new ModelDiscoveryService(new LoggerFactory().CreateLogger<ModelDiscoveryService>()),
            modelRegistry,
            new FakeClock(),
            new LoggerFactory().CreateLogger<ContainerRegistrationService>(),
            autoBenchmark: autoBenchmark);
    }

    [Fact]
    public async Task RegisterAsync_AutoBenchmarkEnabled_TriggersRunForEachNewModel()
    {
        var registry = new FakeContainerRegistry();
        var modelRegistry = new FakeModelRegistry();
        var scheduler = new FakeSchedulerQueue();
        var history = new FakeBenchmarkHistory();
        var auto = new AutoBenchmarkService(
            new FakeSettingsStore(),
            new FakePromptStore(),
            scheduler,
            history,
            new FakeClock(),
            new FakeLogStore(),
            new LoggerFactory().CreateLogger<AutoBenchmarkService>());

        var remote = MakeRemoteWithModels("llama-3-8b", "qwen-7b");
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["agent:gpu1"] = remote });

        var service = MakeRegistrationService(registry, router, new FakeHealthChecker(), modelRegistry, auto);
        var registered = await service.RegisterAsync(new ContainerRegistrationRequest
        {
            DisplayName = "Remote Auto",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        });
        Assert.Equal(ContainerRegistrationStatus.Registered, registered.Container.Status);

        var result = await service.StartAsync(registered.Container.Id);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Equal(2, result.DiscoveredModels.Count);

        // Fire-and-forget runner: wait for both sequential benchmark runs to land.
        await WaitUntilAsync(() =>
        {
            lock (history.Entries) return history.Entries.Count >= 2;
        }, TimeSpan.FromSeconds(10));

        var modelIds = history.AddedModelIds.ToHashSet();
        Assert.Contains("llama-3-8b", modelIds);
        Assert.Contains("qwen-7b", modelIds);
        Assert.All(history.Entries, e => Assert.Equal("completed", e.Status));
        Assert.Equal(2, scheduler.EnqueuedRequests.Count);
    }

    [Fact]
    public async Task RegisterAsync_AutoBenchmarkDisabledBySettings_NeverRuns()
    {
        var registry = new FakeContainerRegistry();
        var modelRegistry = new FakeModelRegistry();
        var scheduler = new FakeSchedulerQueue();
        var history = new FakeBenchmarkHistory();
        var auto = new AutoBenchmarkService(
            new FakeSettingsStore(new Settings { EnableBenchmarking = false }),
            new FakePromptStore(),
            scheduler,
            history,
            new FakeClock(),
            new FakeLogStore(),
            new LoggerFactory().CreateLogger<AutoBenchmarkService>());

        var remote = MakeRemoteWithModels("llama-3-8b");
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["agent:gpu1"] = remote });

        var service = MakeRegistrationService(registry, router, new FakeHealthChecker(), modelRegistry, auto);
        var registered = await service.RegisterAsync(new ContainerRegistrationRequest
        {
            DisplayName = "Remote NoBench",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        });
        Assert.Equal(ContainerRegistrationStatus.Registered, registered.Container.Status);

        var result = await service.StartAsync(registered.Container.Id);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Single(result.DiscoveredModels);

        // Give the fire-and-forget runner ample time to (wrongly) run.
        await Task.Delay(500);

        Assert.Empty(scheduler.EnqueuedRequests);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task RegisterAsync_NoAutoBenchmarkService_RegistrationStillSucceeds()
    {
        // Optional param omitted (existing tests construct it this way): registration
        // must work exactly as before, no benchmarks triggered.
        var registry = new FakeContainerRegistry();
        var modelRegistry = new FakeModelRegistry();
        var scheduler = new FakeSchedulerQueue();

        var remote = MakeRemoteWithModels("llama-3-8b");
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["agent:gpu1"] = remote });

        var service = MakeRegistrationService(registry, router, new FakeHealthChecker(), modelRegistry, autoBenchmark: null);
        var registered = await service.RegisterAsync(new ContainerRegistrationRequest
        {
            DisplayName = "No Auto",
            Image = "vllm-serve",
            ContainerPort = 8000,
            Agent = "gpu1"
        });
        Assert.Equal(ContainerRegistrationStatus.Registered, registered.Container.Status);

        var result = await service.StartAsync(registered.Container.Id);

        Assert.Equal(ContainerRegistrationStatus.Ready, result.Container.Status);
        Assert.Empty(scheduler.EnqueuedRequests);
    }
}
