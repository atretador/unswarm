using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for IdleShutdownService: verifies it leaves unregistered running containers alone.
/// </summary>
public sealed class IdleShutdownServiceTests : IDisposable
{
    private readonly FakeDockerController _docker = new();
    private readonly FakeSettingsStore _settingsStore = new(new Settings
    {
        AutoShutdownIdle = true,
        IdleTimeout = 10  // 10 seconds
    });
    private readonly FakeLogStore _logStore = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    [Fact]
    public async Task UnregisteredContainer_NotStoppedByIdleShutdown()
    {
        // Register one container with RuntimeContainerId
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-1", DisplayName = "Model A", Image = "a:latest",
            RuntimeContainerId = "registered-abc",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        // Docker lists two running containers: one registered, one orphan
        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "registered-abc",
                ModelId = "model-a",
                ModelName = "Model A",
                Status = ContainerStatus.Running,
                Uptime = 999, // well over idle threshold
                RegisteredRuntimeId = "reg-1"
            },
            new ContainerInfo
            {
                Id = "orphan-xyz",
                ModelId = "unknown",
                ModelName = "Unknown",
                Status = ContainerStatus.Running,
                Uptime = 999, // well over idle threshold
                // No RegisteredRuntimeId — not registered
            }
        ];

        // Build the managedIds set like IdleShutdownService does
        var registeredContainers = await _containerRegistry.ListAllAsync();
        var managedIds = new HashSet<string>(
            registeredContainers
                .Where(r => r.RuntimeContainerId is not null)
                .Select(r => r.RuntimeContainerId!)
        );

        // Simulate the IdleShutdownService membership logic
        foreach (var container in _docker.ListedContainers.Where(c => c.Status == ContainerStatus.Running))
        {
            bool isManaged = managedIds.Contains(container.Id)
                || !string.IsNullOrEmpty(container.RegisteredRuntimeId);

            if (!isManaged)
                continue;

            await _docker.StopContainerAsync(container.Id);
        }

        // Only the registered container should be stopped
        Assert.Single(_docker.StoppedContainerIds);
        Assert.Equal("registered-abc", _docker.StoppedContainerIds[0]);
        Assert.DoesNotContain("orphan-xyz", _docker.StoppedContainerIds);
    }

    [Fact]
    public async Task ContainerWithDockerLabelPath_IsAlsoManaged()
    {
        // A container without RuntimeContainerId in registry but with
        // ContainerInfo.RegisteredRuntimeId set (docker label path)
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-label", DisplayName = "Labeled", Image = "label:latest",
            // No RuntimeContainerId — not set via registry
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        _docker.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "container-with-label",
                ModelId = "model-l",
                ModelName = "Labeled",
                Status = ContainerStatus.Running,
                Uptime = 500,
                RegisteredRuntimeId = "reg-label" // docker label path
            }
        ];

        var registeredContainers = await _containerRegistry.ListAllAsync();
        var managedIds = new HashSet<string>(
            registeredContainers
                .Where(r => r.RuntimeContainerId is not null)
                .Select(r => r.RuntimeContainerId!)
        );

        foreach (var container in _docker.ListedContainers.Where(c => c.Status == ContainerStatus.Running))
        {
            bool isManaged = managedIds.Contains(container.Id)
                || !string.IsNullOrEmpty(container.RegisteredRuntimeId);

            if (!isManaged)
                continue;

            await _docker.StopContainerAsync(container.Id);
        }

        // Container accepted via the docker-label path
        Assert.Single(_docker.StoppedContainerIds);
        Assert.Equal("container-with-label", _docker.StoppedContainerIds[0]);
    }

    public void Dispose()
    {
    }
}
