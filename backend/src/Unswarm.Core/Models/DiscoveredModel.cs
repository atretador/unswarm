namespace Unswarm.Core.Models;

public sealed record DiscoveredModel
{
    public required string ModelId { get; init; }
    public string? OwnedBy { get; init; }
}
