using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for the internal HostOnlyDockerControllerRouter (the default router when
/// no agent registry is wired). It is internal to Unswarm.Core and there is no
/// InternalsVisibleTo, so it is instantiated via reflection. Behavior: always
/// routes to the host controller, advertises only "host", always reachable, and
/// ignores incoming messages/disconnects.
/// </summary>
public sealed class HostOnlyDockerControllerRouterTests
{
    private static readonly Type RouterType =
        typeof(IDockerControllerRouter).Assembly.GetType("Unswarm.Core.Services.HostOnlyDockerControllerRouter")!;

    private readonly FakeDockerController _host = new();

    private object CreateRouter() => Activator.CreateInstance(RouterType, [_host])!;

    [Fact]
    public void Type_IsFound()
    {
        Assert.NotNull(RouterType);
    }

    [Fact]
    public void GetController_AnyTarget_ReturnsHostController()
    {
        var router = CreateRouter();
        var method = RouterType.GetMethod("GetController")!;

        var host = method.Invoke(router, [ExecutionTarget.HostId]);
        var agent = method.Invoke(router, ["agent:gpu1"]);

        Assert.Same(_host, host);
        Assert.Same(_host, agent);
    }

    [Fact]
    public void GetAvailableTargets_ReturnsHostOnly()
    {
        var router = CreateRouter();
        var method = RouterType.GetMethod("GetAvailableTargets")!;

        var targets = Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(router, null));

        Assert.Equal([ExecutionTarget.HostId], targets);
    }

    [Fact]
    public void IsTargetReachable_AnyTarget_True()
    {
        var router = CreateRouter();
        var method = RouterType.GetMethod("IsTargetReachable")!;

        Assert.True((bool)method.Invoke(router, [ExecutionTarget.HostId])!);
        Assert.True((bool)method.Invoke(router, ["agent:gpu1"])!);
    }

    [Fact]
    public void IncomingMessageAndDisconnect_AreNoOps()
    {
        var router = CreateRouter();
        var incoming = RouterType.GetMethod("HandleIncomingMessage")!;
        var disconnect = RouterType.GetMethod("NotifyAgentDisconnected")!;

        incoming.Invoke(router, ["gpu1", new AgentMessage { Type = "command_result" }]);
        disconnect.Invoke(router, ["gpu1"]);

        // Reaching here without throwing is the assertion.
        Assert.NotNull(router);
    }
}
