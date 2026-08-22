namespace Unswarm.Core.Contracts;

using Unswarm.Core.Services.Benchmarks;

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
    /// <summary>Benchmark generation cap used when this prompt drives a run.</summary>
    public int MaxTokens { get; init; } = BenchmarkDefaults.MaxTokens;
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

    /// <summary>
    /// Creates a prompt. When <paramref name="maxTokens"/> is null the default cap
    /// (<see cref="BenchmarkDefaults.MaxTokens"/>) is used; otherwise it is clamped
    /// to the sane range (16–32768).
    /// </summary>
    Task<PromptEntry> CreateAsync(string name, string text, int? maxTokens = null, CancellationToken ct = default);

    /// <summary>
    /// Updates a prompt's name and text. Returns the updated entry, or null if the id
    /// does not exist (callers should treat null as 404). When <paramref name="maxTokens"/>
    /// is null the existing cap is kept; otherwise it is clamped to 16–32768.
    /// </summary>
    Task<PromptEntry?> UpdateAsync(string id, string name, string text, int? maxTokens = null, CancellationToken ct = default);

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
