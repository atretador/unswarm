using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Inference request queue monitoring and management. View the scheduler queue state,
/// cancel pending requests, and release conversation-affinity holds.
/// </summary>
/// <remarks>
/// GET /api/queue/snapshot — Get inference queue status
/// DELETE /api/queue/{itemId} — Cancel a queued request (Admin only)
/// POST /api/queue/targets/{targetId}/hold/release — Release conversation holds (Admin only)
/// </remarks>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class QueueController : ControllerBase
{
    private readonly ISchedulerQueue _queue;

    public QueueController(ISchedulerQueue queue) => _queue = queue;

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(CancellationToken ct)
    {
        var snapshot = await _queue.GetSnapshotAsync(ct);
        return Ok(QueueSnapshotResponse.FromSnapshot(snapshot));
    }

    [HttpDelete("{itemId}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CancelItem(string itemId, CancellationToken ct)
    {
        var result = await _queue.CancelItemAsync(itemId, ct);
        if (!result) return NotFound();
        return Ok(new { cancelled = true });
    }

    /// <summary>
    /// Immediately clears all conversation-affinity holds on one target
    /// (user "skip timer"): waiting requests proceed without waiting out the
    /// dwell window.
    /// </summary>
    [HttpPost("targets/{targetId}/hold/release")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReleaseHold(string targetId, CancellationToken ct)
    {
        var result = await _queue.ReleaseConversationHoldsAsync(targetId, ct);
        if (!result) return NotFound();
        return Ok(new { released = true });
    }
}
