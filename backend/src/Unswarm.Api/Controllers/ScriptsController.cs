using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Core.Contracts;
using Unswarm.Core.Helpers;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Remote;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Script management endpoints for both host and remote agents.
///
/// Host scripts (no agent prefix):
///   GET    /api/scripts                        — List host scripts
///   POST   /api/scripts/upload                  — Upload .sh to host scripts dir
///   PUT    /api/scripts/{fileName}              — Update host script
///   GET    /api/scripts/{fileName}/content      — Read host script content
///   DELETE /api/scripts/{fileName}              — Delete host script
///
/// Remote agent scripts:
///   POST   /api/scripts/agent/{agentName}/upload             — Upload .sh to agent
///   PUT    /api/scripts/agent/{agentName}/{fileName}         — Update script on agent
///   GET    /api/scripts/agent/{agentName}/{fileName}/content — Read script content from agent
///   DELETE /api/scripts/agent/{agentName}/{fileName}         — Delete script on agent
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class ScriptsController : ControllerBase
{
    private readonly HostScriptDirectoryService _scriptDir;
    private readonly HostScriptRuntimeController _scriptController;
    private readonly IDockerControllerRouter _router;
    private readonly IAgentRegistry _agentRegistry;
    private readonly ILogger<ScriptsController> _logger;

    public ScriptsController(
        HostScriptDirectoryService scriptDir,
        HostScriptRuntimeController scriptController,
        IDockerControllerRouter router,
        IAgentRegistry agentRegistry,
        ILogger<ScriptsController> logger)
    {
        _scriptDir = scriptDir;
        _scriptController = scriptController;
        _router = router;
        _agentRegistry = agentRegistry;
        _logger = logger;
    }

    // ── Host endpoints ────────────────────────────────────────────────

    /// <summary>
    /// Lists all .sh scripts in the host scripts directory.
    /// </summary>
    [HttpGet]
    public IActionResult List()
    {
        if (HostEnvironment.IsRunningInDocker)
            return BadRequest(LocalizedError.Create("scripts.dockerNotAvailable"));

        var scripts = _scriptDir.ListScripts();
        return Ok(scripts);
    }

    /// <summary>
    /// Uploads a .sh script to the host scripts directory.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(1_048_576)] // 1MB
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (HostEnvironment.IsRunningInDocker)
            return BadRequest(LocalizedError.Create("scripts.dockerNotAvailable"));

        if (file is null || file.Length == 0)
            return BadRequest(LocalizedError.Create("scripts.noFileProvided"));

        try
        {
            using var stream = file.OpenReadStream();
            var info = await _scriptDir.SaveScriptAsync(file.FileName, stream, ct).ConfigureAwait(false);
            _logger.LogInformation("Uploaded script {Name} ({Size} bytes)", info.Name, info.SizeBytes);
            return Ok(info);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(503, LocalizedError.Create("scripts.permissionsError"));
        }
    }

    /// <summary>
    /// Updates an existing script in the host scripts directory.
    /// </summary>
    [HttpPut("{fileName}")]
    [RequestSizeLimit(1_048_576)] // 1MB
    public async Task<IActionResult> Update(string fileName, IFormFile file, CancellationToken ct)
    {
        if (HostEnvironment.IsRunningInDocker)
            return BadRequest(LocalizedError.Create("scripts.dockerNotAvailable"));

        if (file is null || file.Length == 0)
            return BadRequest(LocalizedError.Create("scripts.noFileProvided"));

        try
        {
            using var stream = file.OpenReadStream();
            var info = await _scriptDir.SaveScriptAsync(fileName, stream, ct).ConfigureAwait(false);
            _logger.LogInformation("Updated script {Name} ({Size} bytes)", info.Name, info.SizeBytes);
            return Ok(info);
        }
        catch (FileNotFoundException)
        {
            return NotFound(LocalizedError.Create("scripts.notFound", new { fileName }));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(503, LocalizedError.Create("scripts.permissionsError"));
        }
    }

    /// <summary>
    /// Returns the text content of a host script file.
    /// </summary>
    [HttpGet("{fileName}/content")]
    public async Task<IActionResult> GetContent(string fileName, CancellationToken ct)
    {
        if (HostEnvironment.IsRunningInDocker)
            return BadRequest(LocalizedError.Create("scripts.dockerNotAvailable"));

        try
        {
            var content = await _scriptDir.GetScriptContentAsync(fileName, ct).ConfigureAwait(false);
            return Content(content, "text/plain");
        }
        catch (FileNotFoundException)
        {
            return NotFound(LocalizedError.Create("scripts.notFound", new { fileName }));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a script from the host scripts directory.
    /// Fails if the script is currently running as a registered runtime.
    /// </summary>
    [HttpDelete("{fileName}")]
    public async Task<IActionResult> Delete(string fileName, CancellationToken ct)
    {
        if (HostEnvironment.IsRunningInDocker)
            return BadRequest(LocalizedError.Create("scripts.dockerNotAvailable"));

        try
        {
            await _scriptDir.DeleteScriptAsync(fileName, isRunning: path =>
            {
                return _scriptController.IsRunningByPath(path);
            }, ct).ConfigureAwait(false);

            return Ok(new { deleted = fileName });
        }
        catch (FileNotFoundException)
        {
            return NotFound(LocalizedError.Create("scripts.notFound", new { fileName }));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Remote agent endpoints ────────────────────────────────────────

    /// <summary>
    /// Uploads a .sh script to a remote agent's scripts directory.
    /// </summary>
    [HttpPost("agent/{agentName}/upload")]
    [RequestSizeLimit(1_048_576)] // 1MB
    public async Task<IActionResult> AgentUpload(string agentName, IFormFile file, CancellationToken ct)
    {
        var (remote, remoteError) = ResolveRemoteAgent(agentName);
        if (remote is null)
            return remoteError!;

        if (file is null || file.Length == 0)
            return BadRequest(LocalizedError.Create("scripts.noFileProvided"));

        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var info = await remote.UploadScriptAsync(file.FileName, content, ct).ConfigureAwait(false);
            _logger.LogInformation("Uploaded script {Name} to agent {Agent}", info.Name, agentName);
            return Ok(new { name = info.Name, path = info.Path });
        }
        catch (AgentCommandException ex)
        {
            _logger.LogWarning(ex, "Agent '{Agent}' rejected script upload", agentName);
            return StatusCode(502, LocalizedError.Create("scripts.agentRejected", new { name = agentName, detail = ex.Message }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return StatusCode(503, LocalizedError.Create("scripts.agentFailed", new { name = agentName, detail = ex.Message }));
        }
    }

    /// <summary>
    /// Updates an existing script on a remote agent.
    /// </summary>
    [HttpPut("agent/{agentName}/{fileName}")]
    [RequestSizeLimit(1_048_576)] // 1MB
    public async Task<IActionResult> AgentUpdate(string agentName, string fileName, IFormFile file, CancellationToken ct)
    {
        var (remote, remoteError) = ResolveRemoteAgent(agentName);
        if (remote is null)
            return remoteError!;

        if (file is null || file.Length == 0)
            return BadRequest(LocalizedError.Create("scripts.noFileProvided"));

        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var info = await remote.UpdateScriptAsync(fileName, content, ct).ConfigureAwait(false);
            _logger.LogInformation("Updated script {Name} on agent {Agent}", info.Name, agentName);
            return Ok(new { name = info.Name, path = info.Path });
        }
        catch (AgentCommandException ex)
        {
            _logger.LogWarning(ex, "Agent '{Agent}' rejected script update", agentName);
            return StatusCode(502, LocalizedError.Create("scripts.agentRejected", new { name = agentName, detail = ex.Message }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return StatusCode(503, LocalizedError.Create("scripts.agentFailed", new { name = agentName, detail = ex.Message }));
        }
    }

    /// <summary>
    /// Returns the text content of a script on a remote agent.
    /// </summary>
    [HttpGet("agent/{agentName}/{fileName}/content")]
    public async Task<IActionResult> AgentGetContent(string agentName, string fileName, CancellationToken ct)
    {
        var (remote, remoteError) = ResolveRemoteAgent(agentName);
        if (remote is null)
            return remoteError!;

        try
        {
            var content = await remote.GetScriptContentAsync(fileName, ct).ConfigureAwait(false);
            return Content(content, "text/plain");
        }
        catch (AgentCommandException ex)
        {
            _logger.LogWarning(ex, "Agent '{Agent}' failed to read script content", agentName);
            return StatusCode(502, LocalizedError.Create("scripts.agentRejected", new { name = agentName, detail = ex.Message }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return StatusCode(503, LocalizedError.Create("scripts.agentFailed", new { name = agentName, detail = ex.Message }));
        }
    }

    /// <summary>
    /// Deletes a script from a remote agent's scripts directory.
    /// </summary>
    [HttpDelete("agent/{agentName}/{fileName}")]
    public async Task<IActionResult> AgentDelete(string agentName, string fileName, CancellationToken ct)
    {
        var (remote, remoteError) = ResolveRemoteAgent(agentName);
        if (remote is null)
            return remoteError!;

        try
        {
            await remote.DeleteScriptAsync(fileName, ct).ConfigureAwait(false);
            return Ok(new { deleted = fileName });
        }
        catch (AgentCommandException ex)
        {
            _logger.LogWarning(ex, "Agent '{Agent}' rejected script deletion", agentName);
            return StatusCode(502, LocalizedError.Create("scripts.agentRejected", new { name = agentName, detail = ex.Message }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return StatusCode(503, LocalizedError.Create("scripts.agentFailed", new { name = agentName, detail = ex.Message }));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a remote agent controller. Returns (null, errorAction) on failure.
    /// </summary>
    private (IRemoteDockerController? controller, IActionResult? error) ResolveRemoteAgent(string agentName)
    {
        if (string.Equals(agentName, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase))
            return (null, BadRequest(LocalizedError.Create("scripts.useHostEndpoints")));

        var info = _agentRegistry.GetInfo(agentName);
        if (info is null)
            return (null, NotFound(LocalizedError.Create("agents.notFound", new { name = agentName })));

        var targetId = ExecutionTarget.ForAgent(agentName).Id;
        if (!_router.IsTargetReachable(targetId))
            return (null, StatusCode(503, LocalizedError.Create("agents.notReachable", new { name = agentName })));

        try
        {
            var controller = _router.GetController(targetId);
            return ((IRemoteDockerController)controller, null);
        }
        catch (Exception ex)
        {
            return (null, StatusCode(502, LocalizedError.Create("scripts.agentFailed", new { name = agentName, detail = ex.Message })));
        }
    }
}
