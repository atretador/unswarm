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
        IAgentRegistry? agentRegistry = null,
        HostScriptRuntimeController? scriptRuntime = null)
        => new(
            scriptDir ?? CreateScriptDir(),
            scriptRuntime ?? CreateScriptRuntime(),
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

    // ── Host Update ───────────────────────────────────────────────────

    [Fact]
    public async Task Update_ValidFile_ReturnsOk()
    {
        File.WriteAllText(Path.Combine(_tempDir, "upd.sh"), "#!/bin/bash\necho old");
        var ctrl = CreateController();
        var file = MakeFormFile("upd.sh", "#!/bin/bash\necho new");

        var result = await ctrl.Update("upd.sh", file, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Contains("echo new", File.ReadAllText(Path.Combine(_tempDir, "upd.sh")));
    }

    [Fact]
    public async Task Update_NullFile_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var result = await ctrl.Update("x.sh", null!, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_InvalidFileName_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var file = MakeFormFile("x.txt", "#!/bin/bash\necho hi");
        var result = await ctrl.Update("x.txt", file, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Host GetContent errors ────────────────────────────────────────

    [Fact]
    public async Task GetContent_InvalidFileName_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var result = await ctrl.GetContent("not-a-script.txt", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Host Delete: running script conflict ──────────────────────────

    [Fact]
    public async Task Delete_RunningScript_ReturnsConflict()
    {
        var scriptRuntime = CreateScriptRuntime();
        var scriptPath = Path.Combine(_tempDir, "running.sh");
        File.WriteAllText(scriptPath, "#!/bin/bash\nwhile true; do sleep 1; done");
        File.SetUnixFileMode(scriptPath,
            File.GetUnixFileMode(scriptPath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        var start = await scriptRuntime.StartScriptAsync("running-reg", scriptPath, 9000);
        Assert.NotNull(start.Pid);

        var ctrl = CreateController(scriptRuntime: scriptRuntime);
        var result = await ctrl.Delete("running.sh", CancellationToken.None);

        // The running-script guard must refuse deletion with 409 Conflict.
        Assert.IsType<ConflictObjectResult>(result);
        Assert.True(File.Exists(scriptPath), "running script must not be deleted");

        await scriptRuntime.StopScriptAsync("running-reg");
    }

    // ── Remote agent endpoints (scriptable failures) ──────────────────

    private static FakeDockerControllerRouter RouterFor(string agentName, FakeRemoteDockerControllerCoverage2 remote)
        => new(new Dictionary<string, IDockerController>
        {
            [ExecutionTarget.ForAgent(agentName).Id] = remote
        });

    [Fact]
    public async Task AgentUpdate_Success_ReturnsOk()
    {
        _agentRegistry.RegisteredNames.Add("agent-u");
        var remote = new FakeRemoteDockerControllerCoverage2();
        var ctrl = CreateController(router: RouterFor("agent-u", remote));

        var result = await ctrl.AgentUpdate("agent-u", "my.sh", MakeFormFile("my.sh", "#!/bin/bash\necho hi"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Contains("my.sh", remote.Updated.Select(u => u.Name));
    }

    [Fact]
    public async Task AgentUpdate_NoFile_ReturnsBadRequest()
    {
        _agentRegistry.RegisteredNames.Add("agent-u");
        var ctrl = CreateController(router: RouterFor("agent-u", new FakeRemoteDockerControllerCoverage2()));

        var result = await ctrl.AgentUpdate("agent-u", "my.sh", null!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AgentUpdate_InvalidRouteFileName_ReturnsBadRequest()
    {
        _agentRegistry.RegisteredNames.Add("agent-u");
        var ctrl = CreateController(router: RouterFor("agent-u", new FakeRemoteDockerControllerCoverage2()));

        var result = await ctrl.AgentUpdate("agent-u", "bad.txt", MakeFormFile("bad.txt"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AgentUpdate_AgentRejected_Returns502()
    {
        _agentRegistry.RegisteredNames.Add("agent-u");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            UpdateException = new AgentCommandException("agent-u", "update_script", "nope")
        };
        var ctrl = CreateController(router: RouterFor("agent-u", remote));

        var result = await ctrl.AgentUpdate("agent-u", "my.sh", MakeFormFile("my.sh"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task AgentUpdate_AgentUnavailable_Returns503()
    {
        _agentRegistry.RegisteredNames.Add("agent-u");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            UpdateException = new InvalidOperationException("not connected")
        };
        var ctrl = CreateController(router: RouterFor("agent-u", remote));

        var result = await ctrl.AgentUpdate("agent-u", "my.sh", MakeFormFile("my.sh"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task AgentUpload_InvalidFileName_ReturnsBadRequest()
    {
        _agentRegistry.RegisteredNames.Add("agent-up");
        var ctrl = CreateController(router: RouterFor("agent-up", new FakeRemoteDockerControllerCoverage2()));

        var result = await ctrl.AgentUpload("agent-up", MakeFormFile("bad.txt"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AgentUpload_AgentRejected_Returns502()
    {
        _agentRegistry.RegisteredNames.Add("agent-up");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            UploadException = new AgentCommandException("agent-up", "upload_script", "rejected")
        };
        var ctrl = CreateController(router: RouterFor("agent-up", remote));

        var result = await ctrl.AgentUpload("agent-up", MakeFormFile("ok.sh"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task AgentUpload_AgentUnavailable_Returns503()
    {
        _agentRegistry.RegisteredNames.Add("agent-up");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            UploadException = new TimeoutException("timeout")
        };
        var ctrl = CreateController(router: RouterFor("agent-up", remote));

        var result = await ctrl.AgentUpload("agent-up", MakeFormFile("ok.sh"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task AgentGetContent_AgentRejected_Returns502()
    {
        _agentRegistry.RegisteredNames.Add("agent-gc");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            GetContentException = new AgentCommandException("agent-gc", "get_script", "nope")
        };
        var ctrl = CreateController(router: RouterFor("agent-gc", remote));

        var result = await ctrl.AgentGetContent("agent-gc", "my.sh", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task AgentGetContent_AgentUnavailable_Returns503()
    {
        _agentRegistry.RegisteredNames.Add("agent-gc");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            GetContentException = new InvalidOperationException("not connected")
        };
        var ctrl = CreateController(router: RouterFor("agent-gc", remote));

        var result = await ctrl.AgentGetContent("agent-gc", "my.sh", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task AgentDelete_Success_ReturnsOk()
    {
        _agentRegistry.RegisteredNames.Add("agent-d");
        var remote = new FakeRemoteDockerControllerCoverage2();
        var ctrl = CreateController(router: RouterFor("agent-d", remote));

        var result = await ctrl.AgentDelete("agent-d", "my.sh", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Contains("my.sh", remote.Deleted);
    }

    [Fact]
    public async Task AgentDelete_AgentRejected_Returns502()
    {
        _agentRegistry.RegisteredNames.Add("agent-d");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            DeleteException = new AgentCommandException("agent-d", "delete_script", "nope")
        };
        var ctrl = CreateController(router: RouterFor("agent-d", remote));

        var result = await ctrl.AgentDelete("agent-d", "my.sh", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task AgentDelete_AgentUnavailable_Returns503()
    {
        _agentRegistry.RegisteredNames.Add("agent-d");
        var remote = new FakeRemoteDockerControllerCoverage2
        {
            DeleteException = new InvalidOperationException("not connected")
        };
        var ctrl = CreateController(router: RouterFor("agent-d", remote));

        var result = await ctrl.AgentDelete("agent-d", "my.sh", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task AgentUpload_HostName_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var result = await ctrl.AgentUpload("host", MakeFormFile("x.sh"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AgentUpload_UnreachableAgent_Returns503()
    {
        _agentRegistry.RegisteredNames.Add("agent-x");
        var remote = new FakeRemoteDockerControllerCoverage2();
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { [ExecutionTarget.ForAgent("agent-x").Id] = remote },
            reachable: []);
        var ctrl = CreateController(router: router);

        var result = await ctrl.AgentUpload("agent-x", MakeFormFile("x.sh"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task AgentGetContent_UnknownAgent_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.AgentGetContent("ghost", "x.sh", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }
}
