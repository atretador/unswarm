using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class PromptsController : ControllerBase
{
    private readonly IPromptStore _prompts;

    public PromptsController(IPromptStore prompts)
    {
        _prompts = prompts;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var prompts = await _prompts.ListAsync(ct);
        return Ok(prompts.Select(PromptResponse.From).ToList());
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] PromptUpsertRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "Name and text are required" });

        var entry = await _prompts.CreateAsync(request.Name.Trim(), request.Text.Trim(), ct);
        return CreatedAtAction(nameof(Get), new { id = entry.Id }, PromptResponse.From(entry));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var entry = await _prompts.GetAsync(id, ct);
        if (entry is null) return NotFound();
        return Ok(PromptResponse.From(entry));
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string id, [FromBody] PromptUpsertRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "Name and text are required" });

        var entry = await _prompts.UpdateAsync(id, request.Name.Trim(), request.Text.Trim(), ct);
        if (entry is null) return NotFound();
        return Ok(PromptResponse.From(entry));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var deleted = await _prompts.DeleteAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("{id}/default")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetDefault(string id, CancellationToken ct)
    {
        var entry = await _prompts.SetDefaultAsync(id, ct);
        if (entry is null) return NotFound();
        return Ok(PromptResponse.From(entry));
    }
}
