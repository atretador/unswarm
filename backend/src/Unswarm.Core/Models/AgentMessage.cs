using System.Text.Json;

namespace Unswarm.Core.Models;

public sealed class AgentMessage
{
    public required string Type { get; init; }
    public string? Id { get; init; }
    public string? Agent { get; init; }
    public JsonElement? Payload { get; init; }
}
