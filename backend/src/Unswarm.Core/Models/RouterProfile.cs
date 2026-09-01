namespace Unswarm.Core.Models;

public enum RouterProfileMode
{
    Auto = 0,    // on error, try next model in priority order
    Manual = 1   // user explicitly selects model; no auto-fallback
}

public sealed class RouterProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public RouterProfileMode Mode { get; init; } = RouterProfileMode.Auto;
    public IReadOnlyList<RouterProfileEntry> Entries { get; init; } = [];

    /// <summary>
    /// Stable model identifier of the currently active entry.
    /// Null means use default priority order.
    /// </summary>
    public string? ActiveModelId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class RouterProfileEntry
{
    public required string ModelId { get; init; }  // "cloud/openai/gpt-4o" or local model name
    public int Priority { get; init; }              // lower = tried first
    public bool IsEnabled { get; init; } = true;    // can temporarily disable without removing
}
