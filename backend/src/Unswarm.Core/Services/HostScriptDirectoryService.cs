using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

/// <summary>
/// Manages runtime scripts in the configured host scripts directory.
/// Handles listing, validation, upload (save), and deletion of .sh scripts.
/// Path normalization validates containment within the directory.
/// </summary>
public sealed class HostScriptDirectoryService
{
    private readonly ILogger<HostScriptDirectoryService> _logger;
    private readonly string _scriptsDir;

    public HostScriptDirectoryService(ILogger<HostScriptDirectoryService> logger, IOptions<HostScriptsOptions> options)
    {
        _logger = logger;
        _scriptsDir = options.Value.Directory;
        Directory.CreateDirectory(_scriptsDir);
    }

    /// <summary>
    /// Script info returned by listing and upload operations.
    /// </summary>
    public sealed record ScriptInfo
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public long SizeBytes { get; init; }
        public DateTimeOffset LastModified { get; init; }
    }

    /// <summary>
    /// Lists all .sh files in the configured scripts directory.
    /// Only top-level files are returned (no recursion into subdirectories).
    /// </summary>
    public IReadOnlyList<ScriptInfo> ListScripts()
    {
        if (!Directory.Exists(_scriptsDir))
            return [];

        return Directory.EnumerateFiles(_scriptsDir)
            .Where(f => f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var fi = new FileInfo(f);
                return new ScriptInfo
                {
                    Name = fi.Name,
                    Path = fi.FullName,
                    SizeBytes = fi.Length,
                    LastModified = fi.LastWriteTimeUtc
                };
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Validates a filename for safety: no path traversal, .sh extension only.
    /// </summary>
    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required");

        // Strip any directory components — accept both "foo.sh" and "/full/path/foo.sh"
        fileName = Path.GetFileName(fileName);

        if (!fileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .sh scripts are allowed");

        if (fileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Path traversal is not allowed");

        return fileName;
    }

    /// <summary>
    /// Resolves a filename to an absolute path and validates it stays within the scripts directory.
    /// Normalizes the path before checking containment.
    /// </summary>
    private string ResolveWithinScriptsDir(string fileName)
    {
        fileName = ValidateFileName(fileName);

        var candidate = Path.Combine(_scriptsDir, fileName);

        // Normalize path
        var resolved = Path.GetFullPath(candidate);

        // Containment check: resolved path must start with the scripts directory
        var scriptsDirResolved = Path.GetFullPath(_scriptsDir);
        if (!resolved.StartsWith(scriptsDirResolved, StringComparison.Ordinal) &&
            !resolved.StartsWith(scriptsDirResolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Path is outside the scripts directory: {fileName}");
        }

        return resolved;
    }

    /// <summary>
    /// Saves an uploaded script to the scripts directory.
    /// Validates extension, filename safety, file size, and content permissions.
    /// </summary>
    public async Task<ScriptInfo> SaveScriptAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        var resolved = ResolveWithinScriptsDir(fileName);

        // Size limit: 1MB
        const long maxBytes = 1_048_576;
        if (content.Length > maxBytes)
            throw new ArgumentException($"Script exceeds maximum size of {maxBytes / 1024}KB");

        // Read into memory first so we can validate before writing
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);

        if (buffer.Length > maxBytes)
            throw new ArgumentException($"Script exceeds maximum size of {maxBytes / 1024}KB");

        // Basic sanity: script should start with a shebang or be non-empty
        if (buffer.Length == 0)
            throw new ArgumentException("Script is empty");

        // Write the file
        buffer.Position = 0;
        await File.WriteAllBytesAsync(resolved, buffer.ToArray(), ct).ConfigureAwait(false);

        // Make executable on Linux/macOS — use argument array to avoid shell injection
        try
        {
            var chmod = System.Diagnostics.Process.Start("chmod", ["+x", resolved]);
            if (chmod is not null)
                await chmod.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set executable permission on {Path}", resolved);
        }

        var fi = new FileInfo(resolved);
        _logger.LogInformation("Saved script {Name} ({Size} bytes)", fi.Name, fi.Length);

        return new ScriptInfo
        {
            Name = fi.Name,
            Path = fi.FullName,
            SizeBytes = fi.Length,
            LastModified = fi.LastWriteTimeUtc
        };
    }

    /// <summary>
    /// Reads the text content of a script file.
    /// </summary>
    public async Task<string> GetScriptContentAsync(string fileName, CancellationToken ct = default)
    {
        var resolved = ResolveWithinScriptsDir(fileName);

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Script not found: {fileName}");

        return await File.ReadAllTextAsync(resolved, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a script from the directory. Fails if the script is currently running.
    /// </summary>
    public async Task DeleteScriptAsync(string fileName, Func<string, bool>? isRunning = null, CancellationToken ct = default)
    {
        var resolved = ResolveWithinScriptsDir(fileName);

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Script not found: {fileName}");

        if (isRunning is not null && isRunning(resolved))
            throw new InvalidOperationException($"Cannot delete script '{fileName}': it is currently running. Stop it first.");

        File.Delete(resolved);
        _logger.LogInformation("Deleted script {Path}", resolved);
    }
}
