using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Api.Controllers;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Remote;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for <see cref="ScriptsController"/>.
///
/// Note: <c>HostEnvironment.IsRunningInDocker</c> is a static readonly property
/// evaluated once from an environment variable at type-init time. In the test
/// process it is always <c>false</c>, so host-endpoint guards never trigger.
/// The Docker-guard behaviour is tested indirectly via the integration tests
/// that set <c>RUNNING_IN_DOCKER=true</c> in docker-compose.
/// </summary>
public sealed class ScriptsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeDockerControllerRouter _router;
    private readonly FakeAgentRegistry _agentRegistry;

    public ScriptsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unswarm-scripttests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>());
        _agentRegistry = new FakeAgentRegistry();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private HostScriptDirectoryService CreateScriptDir()
        => new(
            new LoggerFactory().CreateLogger<HostScriptDirectoryService>(),
            Microsoft.Extensions.Options.Options.Create(new HostScriptsOptions { Directory = _tempDir }));

    private HostScriptRuntimeController CreateScriptRuntime()
        => new(new LoggerFactory().CreateLogger<HostScriptRuntimeController>(),
            Path.Combine(_tempDir, "logs"));

    private ScriptsController CreateController(
        HostScriptDirectoryService? scriptDir = null,
        IDockerControllerRouter? router = null,
        IAgentRegistry? agentRegistry = null)
        => new(
            scriptDir ?? CreateScriptDir(),
            CreateScriptRuntime(),
            router ?? _router,
            agentRegistry ?? _agentRegistry,
            new LoggerFactory().CreateLogger<ScriptsController>());

    // ── Helper: create a fake IFormFile ───────────────────────────────

    private static IFormFile MakeFormFile(string fileName, string content = "#!/bin/bash\necho hello")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName);
    }

    // ── List tests ────────────────────────────────────────────────────

    [Fact]
    public void List_ReturnsOkWithScripts()
    {
        // Create a couple of .sh files in the temp dir
        File.WriteAllText(Path.Combine(_tempDir, "alpha.sh"), "#!/bin/bash\necho alpha");
        File.WriteAllText(Path.Combine(_tempDir, "beta.sh"), "#!/bin/bash\necho beta");

        var ctrl = CreateController();
        var result = ctrl.List();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("alpha.sh", json);
        Assert.Contains("beta.sh", json);
    }

    [Fact]
    public void List_EmptyDirectory_ReturnsOkWithEmptyList()
    {
        var ctrl = CreateController();
        var result = ctrl.List();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Equal("[]", json);
    }

    // ── Upload tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Upload_ValidFile_ReturnsOk()
    {
        var ctrl = CreateController();
        var file = MakeFormFile("upload.sh");

        var result = await ctrl.Upload(file, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("upload.sh", json);

        // Verify file was actually written
        Assert.True(File.Exists(Path.Combine(_tempDir, "upload.sh")));
    }

    [Fact]
    public async Task Upload_NullFile_ReturnsBadRequest()
    {
        var ctrl = CreateController();

        var result = await ctrl.Upload(null!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── GetContent tests ──────────────────────────────────────────────

    [Fact]
    public async Task GetContent_ExistingScript_ReturnsContent()
    {
        var expected = "#!/bin/bash\necho hello world";
        File.WriteAllText(Path.Combine(_tempDir, "readme.sh"), expected);

        var ctrl = CreateController();
        var result = await ctrl.GetContent("readme.sh", CancellationToken.None);

        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/plain", contentResult.ContentType);
        Assert.Equal(expected, contentResult.Content);
    }

    [Fact]
    public async Task GetContent_Nonexistent_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.GetContent("nope.sh", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Delete tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingScript_ReturnsOk()
    {
        File.WriteAllText(Path.Combine(_tempDir, "to-delete.sh"), "#!/bin/bash\necho bye");

        var ctrl = CreateController();
        var result = await ctrl.Delete("to-delete.sh", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("to-delete.sh", json);

        Assert.False(File.Exists(Path.Combine(_tempDir, "to-delete.sh")));
    }

    [Fact]
    public async Task Delete_Nonexistent_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.Delete("ghost.sh", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── AgentUpload tests ─────────────────────────────────────────────

    [Fact]
    public async Task AgentUpload_ExistingAgent_ReturnsOk()
    {
        // Register an agent
        _agentRegistry.RegisteredNames.Add("agent1");

        // Make the router return a reachable target with a controller
        var fakeRemote = new FakeRemoteDockerController();
        var agentTargetId = ExecutionTarget.ForAgent("agent1").Id;
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { [agentTargetId] = fakeRemote });

        var ctrl = CreateController(router: router);
        var file = MakeFormFile("remote.sh");

        var result = await ctrl.AgentUpload("agent1", file, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("remote.sh", json);
    }

    [Fact]
    public async Task AgentUpload_UnknownAgent_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var file = MakeFormFile("remote.sh");

        var result = await ctrl.AgentUpload("nonexistent", file, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── AgentGetContent tests ─────────────────────────────────────────

    [Fact]
    public async Task AgentGetContent_ExistingAgent_ReturnsContent()
    {
        _agentRegistry.RegisteredNames.Add("agent2");

        var fakeRemote = new FakeRemoteDockerController();
        var agentTargetId = ExecutionTarget.ForAgent("agent2").Id;
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { [agentTargetId] = fakeRemote });

        var ctrl = CreateController(router: router);

        var result = await ctrl.AgentGetContent("agent2", "test.sh", CancellationToken.None);

        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/plain", contentResult.ContentType);
    }
}
