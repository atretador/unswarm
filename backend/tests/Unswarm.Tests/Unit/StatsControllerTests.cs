using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Tests.Fakes;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

public sealed class StatsControllerTests
{
    private readonly FakeStatsTracker _stats = new();
    private readonly FakeDockerController _docker = new();
    private readonly FakeModelRegistry _registry = new();

    private StatsController CreateController(
        FakeStatsTracker? stats = null,
        FakeDockerController? docker = null,
        FakeModelRegistry? registry = null)
        => new(stats ?? _stats, docker ?? _docker, registry ?? _registry);

    [Fact]
    public async Task Get_ReturnsOkWithStats()
    {
        _stats.SummaryToReturn = new StatsSummary
        {
            TotalRequests = 1000,
            ActiveRequests = 5,
            AvgLatencyMs = 123.4,
            TotalTokensProcessed = 500000,
            QueueDepth = 3
        };

        var ctrl = CreateController();

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<StatsSummaryResponse>(ok.Value);
        Assert.Equal(1000, resp.TotalRequests);
        Assert.Equal(5, resp.ActiveRequests);
        Assert.Equal(123.4, resp.AvgLatencyMs);
        Assert.Equal(500000, resp.TotalTokensProcessed);
        Assert.Equal(3, resp.QueueDepth);
    }

    [Fact]
    public async Task Get_EnrichesContainerCount()
    {
        _docker.ListedContainers =
        [
            new ContainerInfo { Id = "c1", ModelId = "m1", ModelName = "model1", Status = ContainerStatus.Running },
            new ContainerInfo { Id = "c2", ModelId = "m2", ModelName = "model2", Status = ContainerStatus.Running },
            new ContainerInfo { Id = "c3", ModelId = "m3", ModelName = "model3", Status = ContainerStatus.Stopped },
            new ContainerInfo { Id = "c4", ModelId = "m4", ModelName = "model4", Status = ContainerStatus.Error },
        ];

        var ctrl = CreateController();

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<StatsSummaryResponse>(ok.Value);
        // Only Running containers count
        Assert.Equal(2, resp.ContainersRunning);
    }

    [Fact]
    public async Task Get_EnrichesModelCount()
    {
        await _registry.CreateAsync(new ModelDefinition { Id = "m1", Name = "gpt-4" });
        await _registry.CreateAsync(new ModelDefinition { Id = "m2", Name = "claude-3" });
        await _registry.CreateAsync(new ModelDefinition { Id = "m3", Name = "llama-3" });

        var ctrl = CreateController();

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<StatsSummaryResponse>(ok.Value);
        Assert.Equal(3, resp.ModelsLoaded);
    }
}
