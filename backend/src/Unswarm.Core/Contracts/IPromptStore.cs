namespace Unswarm.Core.Contracts;

/// <summary>A single version snapshot of a prompt's text.</summary>
public sealed record PromptVersion
{
    public required string Id { get; init; }
    public required string PromptId { get; init; }
    public required int Version { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A saved benchmark prompt.</summary>
public sealed record PromptEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Text { get; init; }
    public required bool IsDefault { get; init; }
    public int CurrentVersion { get; init; } = 1;
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// CRUD persistence for the named benchmark prompt library.
/// </summary>
public interface IPromptStore
{
    Task<IReadOnlyList<PromptEntry>> ListAsync(CancellationToken ct = default);

    Task<PromptEntry?> GetAsync(string id, CancellationToken ct = default);

    Task<PromptEntry> CreateAsync(string name, string text, CancellationToken ct = default);

    /// <summary>
    /// Updates a prompt's name and text. Returns the updated entry, or null if the id
    /// does not exist (callers should treat null as 404).
    /// </summary>
    Task<PromptEntry?> UpdateAsync(string id, string name, string text, CancellationToken ct = default);

    /// <summary>
    /// Deletes a prompt. Returns true if the row existed, false if the id was unknown.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Sets a prompt as the default benchmark prompt. Clears the flag on all other
    /// prompts first, then sets it on the target. Returns the updated entry, or null
    /// if the id is unknown.
    /// </summary>
    Task<PromptEntry?> SetDefaultAsync(string id, CancellationToken ct = default);

    /// <summary>Returns the current default benchmark prompt, or null if none is set.</summary>
    Task<PromptEntry?> GetDefaultAsync(CancellationToken ct = default);

    /// <summary>Returns all version snapshots for a prompt, ordered by version descending.</summary>
    Task<IReadOnlyList<PromptVersion>> ListVersionsAsync(string promptId, CancellationToken ct = default);

    /// <summary>Returns a specific version of a prompt, or null if not found.</summary>
    Task<PromptVersion?> GetVersionAsync(string promptId, int version, CancellationToken ct = default);
}
