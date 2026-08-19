using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ModelTargetResolverTests
{
    private readonly FakeContainerRegistry _registry = new();
    private readonly ModelTargetResolver _resolver;

    public ModelTargetResolverTests()
    {
        _resolver = new ModelTargetResolver(_registry);
    }

    private async Task RegisterContainerAsync(string id, string image, string agent = "host")
    {
        await _registry.CreateAsync(new RegisteredContainer
        {
            Id = id,
            DisplayName = image,
            Image = image,
            Agent = agent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    [Fact]
    public async Task ResolveTargetAsync_DefaultsToHost_WhenModelUnknown()
    {
        var target = await _resolver.ResolveTargetAsync("unknown-model");
        Assert.Equal("host", target);
    }

    [Fact]
    public async Task ResolveTargetAsync_DefaultsToHost_WhenNoAgentAssigned()
    {
        await RegisterContainerAsync("reg-1", "vllm-1");
        await _registry.AddModelMappingAsync("reg-1", "llama");

        var target = await _resolver.ResolveTargetAsync("llama");

        Assert.Equal("host", target);
    }

    [Fact]
    public async Task ResolveTargetAsync_ReturnsAgentTarget_WhenAgentSet()
    {
        await RegisterContainerAsync("reg-1", "vllm-1", agent: "gpu1");
        await _registry.AddModelMappingAsync("reg-1", "llama");

        var target = await _resolver.ResolveTargetAsync("llama");

        Assert.Equal("agent:gpu1", target);
    }

    [Fact]
    public async Task ResolveTargetAsync_ReturnsHost_WhenAgentIsHost()
    {
        await RegisterContainerAsync("reg-1", "vllm-1", agent: "host");
        await _registry.AddModelMappingAsync("reg-1", "llama");

        var target = await _resolver.ResolveTargetAsync("llama");

        Assert.Equal("host", target);
    }

    [Fact]
    public async Task ResolveTargetAsync_ReturnsHost_WhenContainerMissing()
    {
        // Model maps to a container id that no longer exists
        await _registry.AddModelMappingAsync("ghost", "llama");

        var target = await _resolver.ResolveTargetAsync("llama");

        Assert.Equal("host", target);
    }
}