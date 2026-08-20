namespace Unswarm.Core.Contracts;

/// <summary>A saved benchmark prompt.</summary>
public sealed record PromptEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Text { get; init; }
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
}
