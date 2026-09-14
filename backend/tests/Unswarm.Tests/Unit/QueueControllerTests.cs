using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class QueueControllerTests
{
    private readonly FakeSchedulerQueue _queue = new();

    private QueueController CreateController(ISchedulerQueue? queue = null)
        => new(queue ?? _queue);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task GetSnapshot_ReturnsOkWithSnapshot()
    {
        var ctrl = CreateController();

        var result = await ctrl.GetSnapshot(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<QueueSnapshotResponse>(ok.Value);
        Assert.NotNull(resp.Processing);
        Assert.NotNull(resp.Waiting);
    }

    [Fact]
    public async Task CancelItem_ExistingItem_ReturnsOk()
    {
        // Enqueue an item so CancelItemAsync finds it
        var req = new InferenceRequest
        {
            Id = "item-1",
            ModelName = "test-model",
            OriginalJson = "{}",
            Tcs = new TaskCompletionSource<InferenceResponse>()
        };
        await _queue.EnqueueAsync(req);

        var ctrl = CreateController();

        var result = await ctrl.CancelItem("item-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        // Serialize anonymous type to verify property
        var json = JsonSerializer.Serialize(ok.Value, s_jsonOptions);
        Assert.Contains("\"cancelled\":true", json);
    }

    [Fact]
    public async Task CancelItem_NonExistingItem_ReturnsNotFound()
    {
        var ctrl = CreateController();

        var result = await ctrl.CancelItem("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ReleaseHold_ActiveHold_ReturnsOk()
    {
        var ctrl = CreateController();

        var result = await ctrl.ReleaseHold("target-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        // Serialize anonymous type to verify property
        var json = JsonSerializer.Serialize(ok.Value, s_jsonOptions);
        Assert.Contains("\"released\":true", json);
        Assert.Contains("target-1", _queue.ReleasedHoldTargets);
    }

    [Fact]
    public async Task ReleaseHold_NoHold_ReturnsNotFound()
    {
        // Use a stub that returns false for unknown targets
        var ctrl = CreateController(new StubNoHoldQueue());

        var result = await ctrl.ReleaseHold("unknown-target", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>Stub that always returns false for ReleaseConversationHoldsAsync.</summary>
    private sealed class StubNoHoldQueue : ISchedulerQueue
    {
        public Task<InferenceResponse> EnqueueAsync(InferenceRequest request, CancellationToken ct = default)
            => Task.FromResult(new InferenceResponse { StatusCode = 200 });

        public Task<QueueSnapshot> GetSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(new QueueSnapshot());

        public Task<bool> CancelItemAsync(string itemId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> ReleaseConversationHoldsAsync(string targetId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
