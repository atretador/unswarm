using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Models;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="HostScriptDirectoryService"/>.
/// Each test creates an isolated temp directory and cleans up afterwards.
/// </summary>
public sealed class HostScriptDirectoryServiceTests : IDisposable
{
    private readonly string _tempDir;

    public HostScriptDirectoryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unswarm-script-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private HostScriptDirectoryService CreateService()
        => new(
            new LoggerFactory().CreateLogger<HostScriptDirectoryService>(),
            Options.Create(new HostScriptsOptions { Directory = _tempDir }));

    // ── ListScripts ──────────────────────────────────────────────────

    [Fact]
    public void ListScripts_ReturnsScriptsOrderedByName()
    {
        File.WriteAllText(Path.Combine(_tempDir, "beta.sh"), "#!/bin/bash\necho beta");
        File.WriteAllText(Path.Combine(_tempDir, "alpha.sh"), "#!/bin/bash\necho alpha");
        File.WriteAllText(Path.Combine(_tempDir, "gamma.sh"), "#!/bin/bash\necho gamma");

        var svc = CreateService();
        var result = svc.ListScripts();

        Assert.Equal(3, result.Count);
        Assert.Equal("alpha.sh", result[0].Name);
        Assert.Equal("beta.sh", result[1].Name);
        Assert.Equal("gamma.sh", result[2].Name);
    }

    [Fact]
    public void ListScripts_EmptyDirectory_ReturnsEmptyList()
    {
        var svc = CreateService();
        var result = svc.ListScripts();

        Assert.Empty(result);
    }

    [Fact]
    public void ListScripts_NonExistentDirectory_ReturnsEmptyList()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unswarm-noexist-" + Guid.NewGuid().ToString("N"));
        var svc = new HostScriptDirectoryService(
            new LoggerFactory().CreateLogger<HostScriptDirectoryService>(),
            Options.Create(new HostScriptsOptions { Directory = dir }));

        var result = svc.ListScripts();

        Assert.Empty(result);
    }

    [Fact]
    public void ListScripts_IgnoresNonShFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "script.sh"), "#!/bin/bash\necho ok");
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "not a script");
        File.WriteAllText(Path.Combine(_tempDir, "config.yml"), "key: value");
        File.WriteAllText(Path.Combine(_tempDir, "image.png"), "fake png bytes");

        var svc = CreateService();
        var result = svc.ListScripts();

        Assert.Single(result);
        Assert.Equal("script.sh", result[0].Name);
    }

    [Fact]
    public void ListScripts_ScriptInfoContainsCorrectMetadata()
    {
        var content = "#!/bin/bash\necho hello";
        File.WriteAllText(Path.Combine(_tempDir, "meta.sh"), content);

        var svc = CreateService();
        var result = svc.ListScripts();

        Assert.Single(result);
        var info = result[0];
        Assert.Equal("meta.sh", info.Name);
        Assert.Equal(Path.Combine(_tempDir, "meta.sh"), info.Path);
        Assert.Equal(content.Length, info.SizeBytes);
        Assert.True(info.LastModified > DateTimeOffset.MinValue);
    }

    // ── SaveScriptAsync ──────────────────────────────────────────────

    [Fact]
    public async Task SaveScriptAsync_SavesFileAndReturnsScriptInfo()
    {
        var content = "#!/bin/bash\necho saved";
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        var svc = CreateService();
        var result = await svc.SaveScriptAsync("saved.sh", stream);

        Assert.Equal("saved.sh", result.Name);
        Assert.Equal(content.Length, result.SizeBytes);
        Assert.True(File.Exists(Path.Combine(_tempDir, "saved.sh")));

        // Verify executable bit is set
        var fi = new FileInfo(Path.Combine(_tempDir, "saved.sh"));
        Assert.True((fi.UnixFileMode & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    public async Task SaveScriptAsync_EmptyFile_ThrowsArgumentException()
    {
        var stream = new MemoryStream(Array.Empty<byte>());

        var svc = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveScriptAsync("empty.sh", stream));
    }

    [Fact]
    public async Task SaveScriptAsync_OversizedFile_ThrowsArgumentException()
    {
        // 1MB + 1 byte
        var oversized = new byte[1_048_577];
        var stream = new MemoryStream(oversized);

        var svc = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveScriptAsync("big.sh", stream));
    }

    [Fact]
    public async Task SaveScriptAsync_NonShExtension_ThrowsArgumentException()
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("echo nope"));

        var svc = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveScriptAsync("script.py", stream));
    }

    [Fact]
    public async Task SaveScriptAsync_PathTraversalDoubleDot_StrippedAndSaved()
    {
        // Path.GetFileName strips directory components, so "../escape.sh" → "escape.sh"
        // The traversal is neutralized by stripping, not rejected with an exception.
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("#!/bin/bash\necho hack"));

        var svc = CreateService();
        var result = await svc.SaveScriptAsync("../escape.sh", stream);
        Assert.Equal("escape.sh", result.Name);
    }

    [Fact]
    public async Task SaveScriptAsync_DoubleDotInFilename_ThrowsArgumentException()
    {
        // A filename that literally contains ".." after stripping (e.g. "a..b.sh")
        // should be rejected by ValidateFileName.
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("#!/bin/bash\necho hack"));

        var svc = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveScriptAsync("a..b.sh", stream));
    }

    [Fact]
    public async Task SaveScriptAsync_FullPathStripsToFileName()
    {
        // ValidateFileName strips directory components, so "/full/path/foo.sh" -> "foo.sh"
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("#!/bin/bash\necho ok"));

        var svc = CreateService();
        var result = await svc.SaveScriptAsync("/full/path/foo.sh", stream);

        Assert.Equal("foo.sh", result.Name);
        Assert.True(File.Exists(Path.Combine(_tempDir, "foo.sh")));
    }

    [Fact]
    public async Task SaveScriptAsync_MaxSizeExactlyOneMegabyte_Succeeds()
    {
        // Exactly 1MB — the limit check is > maxBytes, so this should succeed
        var content = new byte[1_048_576];
        Random.Shared.NextBytes(content);
        var stream = new MemoryStream(content);

        var svc = CreateService();
        var result = await svc.SaveScriptAsync("exactly1mb.sh", stream);

        Assert.Equal(1_048_576, result.SizeBytes);
    }

    // ── GetScriptContentAsync ────────────────────────────────────────

    [Fact]
    public async Task GetScriptContentAsync_ExistingFile_ReturnsContent()
    {
        var expected = "#!/bin/bash\necho hello";
        File.WriteAllText(Path.Combine(_tempDir, "read.sh"), expected);

        var svc = CreateService();
        var content = await svc.GetScriptContentAsync("read.sh");

        Assert.Equal(expected, content);
    }

    [Fact]
    public async Task GetScriptContentAsync_MissingFile_ThrowsFileNotFoundException()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.GetScriptContentAsync("nope.sh"));
    }

    // ── DeleteScriptAsync ────────────────────────────────────────────

    [Fact]
    public async Task DeleteScriptAsync_ExistingFile_DeletesFile()
    {
        var path = Path.Combine(_tempDir, "to-delete.sh");
        File.WriteAllText(path, "#!/bin/bash\necho bye");

        var svc = CreateService();
        await svc.DeleteScriptAsync("to-delete.sh");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteScriptAsync_MissingFile_ThrowsFileNotFoundException()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.DeleteScriptAsync("ghost.sh"));
    }

    [Fact]
    public async Task DeleteScriptAsync_ScriptIsRunning_ThrowsInvalidOperationException()
    {
        var path = Path.Combine(_tempDir, "running.sh");
        File.WriteAllText(path, "#!/bin/bash\nsleep 999");

        var svc = CreateService();
        var isRunning = (string resolved) => true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteScriptAsync("running.sh", isRunning));

        // File should still exist since delete was blocked
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteScriptAsync_ScriptNotRunning_DeletesFile()
    {
        var path = Path.Combine(_tempDir, "stoppable.sh");
        File.WriteAllText(path, "#!/bin/bash\necho done");

        var svc = CreateService();
        var isRunning = (string resolved) => false;

        await svc.DeleteScriptAsync("stoppable.sh", isRunning);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteScriptAsync_NullIsRunning_DeletesFile()
    {
        var path = Path.Combine(_tempDir, "free.sh");
        File.WriteAllText(path, "#!/bin/bash\necho free");

        var svc = CreateService();

        // No isRunning callback — should just delete
        await svc.DeleteScriptAsync("free.sh");

        Assert.False(File.Exists(path));
    }

    // ── ScriptInfo properties ────────────────────────────────────────

    [Fact]
    public async Task SaveScriptAsync_ReportsCorrectSizeBytes()
    {
        var content = "short";
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        var svc = CreateService();
        var info = await svc.SaveScriptAsync("size.sh", stream);

        Assert.Equal(content.Length, info.SizeBytes);
    }
}
