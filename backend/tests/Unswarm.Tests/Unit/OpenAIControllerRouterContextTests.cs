using Unswarm.Api.Controllers;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for <see cref="OpenAIController.ComputeRouterContextWindow"/> — the
/// effective context window advertised for a router profile, which must be the
/// minimum across all of its enabled entries (local and cloud) so clients
/// compact before overrunning the smallest model.
/// </summary>
public sealed class OpenAIControllerRouterContextTests
{
    private static RouterProfileEntry Entry(string modelId, int priority = 0, bool enabled = true)
        => new() { ModelId = modelId, Priority = priority, IsEnabled = enabled };

    private static ModelDefinition Local(string id, string name, int context, string? displayName = null)
        => new()
        {
            Id = id,
            Name = name,
            DisplayName = displayName,
            ContextWindow = context,
            Status = ModelStatus.Ready,
        };

    [Fact]
    public void ReturnsLowestAcrossLocalAndCloudEntries()
    {
        var entries = new[] { Entry("local-256k"), Entry("cloud/provider/small-128k") };
        var cloud = new Dictionary<string, int> { ["cloud/provider/small-128k"] = 131072 };
        var local = new[] { Local("runtime-a:local-256k", "local-256k", 262144) };

        var result = OpenAIController.ComputeRouterContextWindow(entries, cloud, local);

        Assert.Equal(131072, result);
    }

    [Fact]
    public void ResolvesLocalEntryByCompositeIdNameAndDisplayName()
    {
        var local = new[]
        {
            Local("runtime-a:qwen2.5-7b-instruct", "qwen2.5-7b-instruct", 131072, "qwen-7b"),
            Local("runtime-b:llama-70b", "llama-70b", 262144, "llama"),
        };

        Assert.Equal(131072, OpenAIController.ComputeRouterContextWindow(
            [Entry("runtime-a:qwen2.5-7b-instruct")], new Dictionary<string, int>(), local));
        Assert.Equal(131072, OpenAIController.ComputeRouterContextWindow(
            [Entry("qwen2.5-7b-instruct")], new Dictionary<string, int>(), local));
        Assert.Equal(131072, OpenAIController.ComputeRouterContextWindow(
            [Entry("qwen-7b")], new Dictionary<string, int>(), local));
    }

    [Fact]
    public void ReturnsMinimumWhenSmallerEntryIsCloud()
    {
        var entries = new[] { Entry("cloud/provider/small-128k"), Entry("big-local") };
        var cloud = new Dictionary<string, int> { ["cloud/provider/small-128k"] = 131072 };
        var local = new[] { Local("runtime-a:big-local", "big-local", 262144) };

        Assert.Equal(131072, OpenAIController.ComputeRouterContextWindow(entries, cloud, local));
    }

    [Fact]
    public void IgnoresDisabledEntries()
    {
        var entries = new[] { Entry("small-128k", enabled: false), Entry("big-256k") };
        var local = new[]
        {
            Local("runtime-a:small-128k", "small-128k", 131072),
            Local("runtime-a:big-256k", "big-256k", 262144),
        };

        Assert.Equal(262144, OpenAIController.ComputeRouterContextWindow(entries, new Dictionary<string, int>(), local));
    }

    [Fact]
    public void IgnoresUnknownContextWindows()
    {
        var entries = new[] { Entry("unknown-local"), Entry("known-256k"), Entry("cloud/provider/unknown") };
        var cloud = new Dictionary<string, int> { ["cloud/provider/unknown"] = 0 };
        var local = new[]
        {
            Local("runtime-a:unknown-local", "unknown-local", 0),
            Local("runtime-a:known-256k", "known-256k", 262144),
        };

        Assert.Equal(262144, OpenAIController.ComputeRouterContextWindow(entries, cloud, local));
    }

    [Fact]
    public void ReturnsZeroWhenNoEntryHasKnownContext()
    {
        var entries = new[] { Entry("missing-local"), Entry("cloud/provider/unknown") };
        var cloud = new Dictionary<string, int> { ["cloud/provider/unknown"] = 0 };

        Assert.Equal(0, OpenAIController.ComputeRouterContextWindow(entries, cloud, []));
    }
}
