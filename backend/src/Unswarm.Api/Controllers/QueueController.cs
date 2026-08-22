using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Controllers;

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
    public async Task<IActionResult> CancelItem(string itemId, CancellationToken ct)
    {
        var result = await _queue.CancelItemAsync(itemId, ct);
        if (!result) return NotFound();
        return Ok(new { cancelled = true });
    }
}
