using Unswarm.Api.Controllers;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for <see cref="OpenAIController.ComputeRouterModalities"/> — the
/// effective input modalities advertised for a router profile, which must be
/// the intersection across all of its enabled entries (local and cloud). An
/// unresolvable or unknown entry conservatively contributes text-only.
/// </summary>
public sealed class OpenAIControllerRouterModalitiesTests
{
    private static RouterProfileEntry Entry(string modelId, int priority = 0, bool enabled = true)
        => new() { ModelId = modelId, Priority = priority, IsEnabled = enabled };

    private static ModelDefinition Local(string id, string name, string[]? modalities, string? displayName = null)
        => new()
        {
            Id = id,
            Name = name,
            DisplayName = displayName,
            InputModalitiesJson = modalities is null ? null : ModelModalities.SerializeJson(modalities),
            Status = ModelStatus.Ready,
        };

    private static IReadOnlyDictionary<string, string[]> Cloud(params (string Id, string[] Modalities)[] entries)
        => entries.ToDictionary(e => e.Id, e => e.Modalities, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ReturnsIntersectionAcrossLocalAndCloudEntries()
    {
        var entries = new[] { Entry("local-multi"), Entry("cloud/provider/vision") };
        var cloud = Cloud(("cloud/provider/vision", ["text", "image"]));
        var local = new[] { Local("runtime-a:local-multi", "local-multi", ["text", "image", "video"]) };

        var result = OpenAIController.ComputeRouterModalities(entries, cloud, local);

        Assert.Equal(["text", "image"], result);
    }

    [Fact]
    public void ResolvesLocalEntryByCompositeIdNameAndDisplayName()
    {
        var local = new[]
        {
            Local("runtime-a:qwen2.5-7b-instruct", "qwen2.5-7b-instruct", ["text", "image"], "qwen-7b"),
        };

        Assert.Equal(["text", "image"], OpenAIController.ComputeRouterModalities(
            [Entry("runtime-a:qwen2.5-7b-instruct")], Cloud(), local));
        Assert.Equal(["text", "image"], OpenAIController.ComputeRouterModalities(
            [Entry("qwen2.5-7b-instruct")], Cloud(), local));
        Assert.Equal(["text", "image"], OpenAIController.ComputeRouterModalities(
            [Entry("qwen-7b")], Cloud(), local));
    }

    [Fact]
    public void IgnoresDisabledEntries()
    {
        var entries = new[] { Entry("narrow", enabled: false), Entry("wide") };
        var local = new[]
        {
            Local("runtime-a:narrow", "narrow", ["text"]),
            Local("runtime-a:wide", "wide", ["text", "image", "audio"]),
        };

        var result = OpenAIController.ComputeRouterModalities(entries, Cloud(), local);

        Assert.Equal(["text", "image", "audio"], result);
    }

    [Fact]
    public void UnknownEntryForcesTextOnly()
    {
        var entries = new[] { Entry("known"), Entry("unknown-local") };
        var local = new[] { Local("runtime-a:known", "known", ["text", "image"]) };

        var result = OpenAIController.ComputeRouterModalities(entries, Cloud(), local);

        Assert.Equal(["text"], result);
    }

    [Fact]
    public void UnknownCloudEntryForcesTextOnly()
    {
        var entries = new[] { Entry("known"), Entry("cloud/provider/missing") };
        var local = new[] { Local("runtime-a:known", "known", ["text", "image"]) };

        var result = OpenAIController.ComputeRouterModalities(entries, Cloud(), local);

        Assert.Equal(["text"], result);
    }

    [Fact]
    public void PropagatesPdfVideoAndAudioInCanonicalOrder()
    {
        var entries = new[] { Entry("multimodal") };
        var local = new[] { Local("runtime-a:multimodal", "multimodal", ["pdf", "video", "audio", "text"]) };

        var result = OpenAIController.ComputeRouterModalities(entries, Cloud(), local);

        Assert.Equal(["text", "video", "audio", "pdf"], result);
    }

    [Fact]
    public void NoEnabledEntriesReturnsTextOnly()
    {
        var entries = new[] { Entry("a", enabled: false), Entry("b", enabled: false) };
        var local = new[]
        {
            Local("runtime-a:a", "a", ["text", "image"]),
            Local("runtime-a:b", "b", ["text", "video"]),
        };

        var result = OpenAIController.ComputeRouterModalities(entries, Cloud(), local);

        Assert.Equal(["text"], result);
    }

    [Fact]
    public void EmptyEntriesReturnsTextOnly()
    {
        var result = OpenAIController.ComputeRouterModalities([], Cloud(), []);

        Assert.Equal(["text"], result);
    }

    [Fact]
    public void LocalModelWithoutModalitiesDefaultsToTextOnly()
    {
        var entries = new[] { Entry("legacy") };
        var local = new[] { Local("runtime-a:legacy", "legacy", modalities: null) };

        var result = OpenAIController.ComputeRouterModalities(entries, Cloud(), local);

        Assert.Equal(["text"], result);
    }

    [Fact]
    public void EmptyCloudModalitiesDefaultToTextOnly()
    {
        var entries = new[] { Entry("cloud/provider/empty") };
        var cloud = Cloud(("cloud/provider/empty", []));

        var result = OpenAIController.ComputeRouterModalities(entries, cloud, []);

        Assert.Equal(["text"], result);
    }

    [Fact]
    public void SingleUnknownTokenInListIsDroppedNotFatal()
    {
        // Stored/discovered data is normalized gracefully: unknown tokens fall away.
        var entries = new[] { Entry("scanned") };
        var local = new[] { Local("runtime-a:scanned", "scanned", ["text", "hologram"]) };

        var result = OpenAIController.ComputeRouterModalities(entries, Cloud(), local);

        Assert.Equal(["text"], result);
    }
}
