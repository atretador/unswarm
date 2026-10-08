using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Tests.Fakes;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

public sealed class LogsControllerTests
{
    private readonly FakeLogStore _logStore = new();

    private LogsController CreateController(FakeLogStore? logStore = null)
        => new(logStore ?? _logStore);

    [Fact]
    public async Task Get_ReturnsOkWithEntries()
    {
        _logStore.Enqueue(LogLevel.Info, "scheduler", "Request completed");
        _logStore.Enqueue(LogLevel.Error, "docker", "Container failed");

        var ctrl = CreateController();

        var result = await ctrl.Get(ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var entries = Assert.IsAssignableFrom<List<LogEntryResponse>>(ok.Value);
        Assert.Equal(2, entries.Count);
        Assert.Equal("scheduler", entries[0].Source);
        Assert.Equal("docker", entries[1].Source);
    }

    [Fact]
    public async Task Get_FiltersBySource()
    {
        _logStore.Enqueue(LogLevel.Info, "scheduler", "msg1");
        _logStore.Enqueue(LogLevel.Info, "docker", "msg2");
        _logStore.Enqueue(LogLevel.Info, "scheduler", "msg3");

        var ctrl = CreateController();

        var result = await ctrl.Get(source: "scheduler", ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var entries = Assert.IsAssignableFrom<List<LogEntryResponse>>(ok.Value);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("scheduler", e.Source));
    }

    [Fact]
    public async Task Get_FiltersByLevel()
    {
        _logStore.Enqueue(LogLevel.Info, "app", "info msg");
        _logStore.Enqueue(LogLevel.Error, "app", "error msg");
        _logStore.Enqueue(LogLevel.Warn, "app", "warn msg");

        var ctrl = CreateController();

        var result = await ctrl.Get(level: LogLevel.Error, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var entries = Assert.IsAssignableFrom<List<LogEntryResponse>>(ok.Value);
        Assert.Single(entries);
        Assert.Equal(LogLevel.Error, entries[0].Level);
    }

    [Fact]
    public async Task Get_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            _logStore.Enqueue(LogLevel.Info, "app", $"msg{i}");

        var ctrl = CreateController();

        var result = await ctrl.Get(limit: 3, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var entries = Assert.IsAssignableFrom<List<LogEntryResponse>>(ok.Value);
        Assert.Equal(3, entries.Count);
    }

    // ── SSE stream ────────────────────────────────────────────────────

    [Fact]
    public async Task Stream_WritesEntriesAsSse()
    {
        var store = new FakeLogStoreCoverage1();
        store.Enqueue(LogLevel.Info, "app", "hello-stream");

        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var controller = new LogsController(store)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        await controller.Stream(CancellationToken.None);

        body.Position = 0;
        var text = new StreamReader(body).ReadToEnd();
        Assert.Contains("data:", text);
        Assert.Contains("hello-stream", text);
        Assert.Equal("text/event-stream", context.Response.ContentType);
    }

    [Fact]
    public async Task Stream_NoEntries_WritesNothing()
    {
        var store = new FakeLogStoreCoverage1();
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var controller = new LogsController(store)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        await controller.Stream(CancellationToken.None);

        Assert.Equal(0, body.Length);
    }
}
