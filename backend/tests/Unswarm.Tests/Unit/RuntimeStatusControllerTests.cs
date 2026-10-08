using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Core.Contracts;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for RuntimeStatusController.Stream — the SSE endpoint that pushes
/// agent runtime status changes. Uses a channel-backed broadcaster so the
/// stream ends deterministically once completed.
/// </summary>
public sealed class RuntimeStatusControllerTests
{
    private static (RuntimeStatusController Controller, MemoryStream Body, DefaultHttpContext Context) CreateController(
        FakeRuntimeStatusBroadcasterCoverage1 broadcaster)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var controller = new RuntimeStatusController(broadcaster)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        return (controller, body, context);
    }

    [Fact]
    public async Task Stream_WritesPublishedEventsAsSse()
    {
        var broadcaster = new FakeRuntimeStatusBroadcasterCoverage1();
        broadcaster.Publish(new RuntimeStatusEvent(
            "agent-x",
            DateTimeOffset.UtcNow,
            [new ContainerStatusUpdate("c1", "llama", "running", 8080)],
            [new ScriptStatusUpdate("/opt/x.sh", "running", "r1", 9000)]));
        broadcaster.Complete();

        var (controller, body, context) = CreateController(broadcaster);

        await controller.Stream(CancellationToken.None);

        body.Position = 0;
        var text = new StreamReader(body).ReadToEnd();
        Assert.Contains("data:", text);
        Assert.Contains("agent-x", text);
        Assert.Contains("llama", text);
        Assert.Equal("text/event-stream", context.Response.Headers["Content-Type"].ToString());
    }

    [Fact]
    public async Task Stream_NoEvents_CompletesWithoutWriting()
    {
        var broadcaster = new FakeRuntimeStatusBroadcasterCoverage1();
        broadcaster.Complete();

        var (controller, body, _) = CreateController(broadcaster);

        await controller.Stream(CancellationToken.None);

        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task Stream_PreCancelledToken_CompletesQuietly()
    {
        var broadcaster = new FakeRuntimeStatusBroadcasterCoverage1();
        var (controller, body, _) = CreateController(broadcaster);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await controller.Stream(cts.Token);

        Assert.Equal(0, body.Length);
    }
}
