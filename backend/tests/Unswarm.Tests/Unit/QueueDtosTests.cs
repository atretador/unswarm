using Unswarm.Api.Dtos;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Mapping tests for the queue snapshot DTO layer: multi-lane Processing list,
/// CurrentSlot compatibility alias, per-item lane fields (RuntimeId,
/// BlockedByRuntimeIds), transition RuntimeId, and root-level skip budget state.
/// </summary>
public sealed class QueueDtosTests
{
    private static QueueItem MakeItem(
        string id,
        string model,
        QueueItemStatus status,
        string? runtimeId = null,
        IReadOnlyList<string>? blockedBy = null,
        DateTimeOffset? createdAt = null)
        => new()
        {
            Id = id,
            ModelRequested = model,
            TargetId = "host",
            RuntimeId = runtimeId,
            BlockedByRuntimeIds = blockedBy ?? [],
            Status = status,
            Priority = 0,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public void FromSnapshot_MapsProcessingList_AndCurrentSlotAlias()
    {
        var older = MakeItem("p1", "model-a", QueueItemStatus.Processing, runtimeId: "reg-a",
            createdAt: DateTimeOffset.UtcNow.AddSeconds(-2));
        var newer = MakeItem("p2", "model-m", QueueItemStatus.Processing, runtimeId: "reg-m",
            createdAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        var snapshot = new QueueSnapshot
        {
            CurrentSlot = older, // worker keeps the oldest processing item as compat alias
            Processing = [older, newer]
        };

        var dto = QueueSnapshotResponse.FromSnapshot(snapshot);

        Assert.Equal(2, dto.Processing.Count);
        Assert.Equal(["p1", "p2"], dto.Processing.Select(i => i.Id).ToList());
        // Backward compatibility: CurrentSlot still points at the oldest processing item.
        Assert.NotNull(dto.CurrentSlot);
        Assert.Equal("p1", dto.CurrentSlot.Id);
    }

    [Fact]
    public void FromSnapshot_EmptyProcessing_LeavesCurrentSlotNull()
    {
        var snapshot = new QueueSnapshot
        {
            Waiting = [MakeItem("w1", "model-a", QueueItemStatus.Waiting)]
        };

        var dto = QueueSnapshotResponse.FromSnapshot(snapshot);

        Assert.Empty(dto.Processing);
        Assert.Null(dto.CurrentSlot);
        Assert.Single(dto.Waiting);
    }

    [Fact]
    public void FromItem_MapsRuntimeIdAndBlockedByRuntimeIds()
    {
        var item = MakeItem("w1", "model-c", QueueItemStatus.Waiting,
            runtimeId: "reg-c", blockedBy: ["reg-a", "reg-b"]);

        var dto = QueueItemResponse.FromItem(item);

        Assert.Equal("reg-c", dto.RuntimeId);
        Assert.Equal(["reg-a", "reg-b"], dto.BlockedByRuntimeIds);
    }

    [Fact]
    public void FromItem_DefaultsLaneFieldsForUnroutedItems()
    {
        var item = MakeItem("x1", "model-x", QueueItemStatus.Failed);
        item = item with { ErrorMessage = "boom" };

        var dto = QueueItemResponse.FromItem(item);

        Assert.Null(dto.RuntimeId);
        Assert.Empty(dto.BlockedByRuntimeIds);
        Assert.Equal(QueueItemStatus.Failed, dto.Status);
        Assert.Equal("boom", dto.ErrorMessage);
    }

    [Fact]
    public void FromTransition_MapsRuntimeId()
    {
        var transition = new ModelTransition
        {
            Id = "t1",
            FromModel = "model-a",
            ToModel = "model-b",
            RuntimeId = "reg-b",
            Status = "switching",
            StartedAt = DateTimeOffset.UtcNow
        };

        var dto = ModelTransitionResponse.FromTransition(transition);

        Assert.Equal("t1", dto.Id);
        Assert.Equal("model-a", dto.FromModel);
        Assert.Equal("model-b", dto.ToModel);
        Assert.Equal("reg-b", dto.RuntimeId);
        Assert.Equal("switching", dto.Status);
    }

    [Fact]
    public void FromSnapshot_MapsSkipBudgetState()
    {
        var snapshot = new QueueSnapshot { SkipsUsed = 2, SkipsRemaining = 1 };

        var dto = QueueSnapshotResponse.FromSnapshot(snapshot);

        Assert.Equal(2, dto.SkipsUsed);
        Assert.Equal(1, dto.SkipsRemaining);
    }
}
