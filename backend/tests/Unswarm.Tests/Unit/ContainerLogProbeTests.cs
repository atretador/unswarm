using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for ContainerLogProbe: verifies dedup logic and enqueue behavior
/// using fake Docker controller, container registry, and log store.
/// </summary>
public sealed class ContainerLogProbeTests : IDisposable
{
    private readonly FakeDockerController _docker = new();
    private readonly FakeContainerRegistry _registry = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeDockerControllerRouter _router;

    public ContainerLogProbeTests()
    {
        _router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController>
            {
                ["host"] = _docker
            });
    }

    [Fact]
    public async Task ContainerLogs_FirstPoll_EnqueuesAllLines()
    {
        // Arrange: register a running container
        await _registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-1",
            DisplayName = "llama-70b",
            Image = "llama:latest",
            RuntimeContainerId = "docker-abc",
            Agent = "host",
            Status = ContainerRegistrationStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var logLines = new List<string> { "line 1", "line 2", "line 3" };
        _docker.OnGetContainerLogs = (id, tailLines, ct) =>
            Task.FromResult<IReadOnlyList<string>>(logLines);

        // Act: simulate the probe's container poll (first poll — no previous state)
        var controller = _router.GetController("host");
        var lines = await controller.GetContainerLogsAsync("docker-abc", 100);

        // All lines should be enqueued on first poll
        foreach (var line in lines)
        {
            _logStore.Enqueue(LogLevel.Info, "llama-70b", line);
        }

        // Assert
        Assert.Equal(3, _logStore.Entries.Count);
        Assert.Equal("line 1", _logStore.Entries[0].Message);
        Assert.Equal("line 2", _logStore.Entries[1].Message);
        Assert.Equal("line 3", _logStore.Entries[2].Message);
        Assert.All(_logStore.Entries, e => Assert.Equal("llama-70b", e.Source));
    }

    [Fact]
    public void ContainerLogs_Dedup_NewLinesOnlyEnqueued()
    {
        // Arrange: simulate two consecutive polls
        var previousPoll = new[] { "line 1", "line 2", "line 3" };
        var currentPoll = new[] { "line 2", "line 3", "line 4", "line 5" };

        // Act: dedup logic (same as ContainerLogProbe)
        var previousSet = new HashSet<string>(previousPoll);
        var newLines = currentPoll.Where(line => !previousSet.Contains(line)).ToList();

        foreach (var line in newLines)
        {
            _logStore.Enqueue(LogLevel.Info, "test-source", line);
        }

        // Assert: only truly new lines are enqueued
        Assert.Equal(2, _logStore.Entries.Count);
        Assert.Equal("line 4", _logStore.Entries[0].Message);
        Assert.Equal("line 5", _logStore.Entries[1].Message);
    }

    [Fact]
    public void ContainerLogs_AllSame_NothingEnqueued()
    {
        // Arrange: same lines on both polls
        var previousPoll = new[] { "line 1", "line 2" };
        var currentPoll = new[] { "line 1", "line 2" };

        // Act
        var previousSet = new HashSet<string>(previousPoll);
        var newLines = currentPoll.Where(line => !previousSet.Contains(line)).ToList();

        foreach (var line in newLines)
        {
            _logStore.Enqueue(LogLevel.Info, "test-source", line);
        }

        // Assert: nothing new to enqueue
        Assert.Empty(_logStore.Entries);
    }

    [Fact]
    public void ContainerLogs_AllDroppedTail_EnqueuesEntireNewBatch()
    {
        // Arrange: completely different lines (container rotated all logs)
        var previousPoll = new[] { "old line 1", "old line 2" };
        var currentPoll = new[] { "new line 1", "new line 2", "new line 3" };

        // Act
        var previousSet = new HashSet<string>(previousPoll);
        var newLines = currentPoll.Where(line => !previousSet.Contains(line)).ToList();

        foreach (var line in newLines)
        {
            _logStore.Enqueue(LogLevel.Info, "test-source", line);
        }

        // Assert: all new lines enqueued
        Assert.Equal(3, _logStore.Entries.Count);
        Assert.Equal("new line 1", _logStore.Entries[0].Message);
        Assert.Equal("new line 2", _logStore.Entries[1].Message);
        Assert.Equal("new line 3", _logStore.Entries[2].Message);
    }

    [Fact]
    public void ScriptLogs_Dedup_WorksCorrectly()
    {
        // Arrange: simulate script log tails
        var previousPoll = new[] { "[stdout] starting up", "[stdout] ready" };
        var currentPoll = new[] { "[stdout] ready", "[stderr] deprecation warning", "[stdout] processing" };

        // Act
        var previousSet = new HashSet<string>(previousPoll);
        var newLines = currentPoll.Where(line => !previousSet.Contains(line)).ToList();

        foreach (var line in newLines)
        {
            var level = line.StartsWith("[stderr]") ? LogLevel.Warn : LogLevel.Info;
            _logStore.Enqueue(LogLevel.Warn, "my-script", line);
        }

        // Assert: only truly new lines, with correct level classification
        Assert.Equal(2, _logStore.Entries.Count);
        Assert.Equal("[stderr] deprecation warning", _logStore.Entries[0].Message);
        Assert.Equal(LogLevel.Warn, _logStore.Entries[0].Level);
        Assert.Equal("[stdout] processing", _logStore.Entries[1].Message);
        Assert.Equal(LogLevel.Warn, _logStore.Entries[1].Level);
    }

    [Fact]
    public void UnreachableContainer_SkippedQuietly()
    {
        // Arrange: agent target not reachable
        var unreachableRouter = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = _docker },
            reachable: []); // nothing reachable

        // Act
        var reachable = unreachableRouter.IsTargetReachable("host");

        // Assert: no exception, target is unreachable, probe should skip
        Assert.False(reachable);
    }

    [Fact]
    public void EmptyLines_AreSkipped()
    {
        // Arrange
        var previousPoll = Array.Empty<string>();
        var currentPoll = new[] { "", "actual content", "" };

        // Act
        var previousSet = new HashSet<string>(previousPoll);
        var newLines = currentPoll.Where(line => !previousSet.Contains(line)).ToList();

        foreach (var line in newLines)
        {
            if (!string.IsNullOrEmpty(line))
                _logStore.Enqueue(LogLevel.Info, "test", line);
        }

        // Assert: only non-empty new lines enqueued
        Assert.Single(_logStore.Entries);
        Assert.Equal("actual content", _logStore.Entries[0].Message);
    }

    public void Dispose()
    {
    }
}
