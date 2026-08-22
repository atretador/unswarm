using System.Text.Json;
using System.Text.Json.Serialization;
using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

/// <summary>
/// Enum converter that emits camelCase strings ("inference", "agent").
/// The 2-arg attribute form does not compile on this TFM (CS1729), so we
/// define a dedicated converter class.
/// </summary>
internal sealed class CamelCaseEnumJsonConverter : JsonStringEnumConverter
{
    public CamelCaseEnumJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>
/// Create request. <paramref name="BoundAgentName"/> is optional: when non-empty
/// the new agent key is permanently bound to that agent name; when null/empty
/// the key stays unbound (an unbound agent-scope key binds to the first
/// agent_name claimed in the /ws/agent handshake).
/// </summary>
public record CreateApiKeyRequest(string Name, string? BoundAgentName = null);

public sealed class ApiKeyCreateResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    [JsonConverter(typeof(CamelCaseEnumJsonConverter))]
    public ApiKeyScope Scope { get; set; }
    public bool IsActive { get; set; }
    public string? BoundAgentName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>Presented exactly once, at creation/rotation. Never returned again.</summary>
    public string Secret { get; set; } = string.Empty;
}

public sealed class ApiKeyListItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    [JsonConverter(typeof(CamelCaseEnumJsonConverter))]
    public ApiKeyScope Scope { get; set; }
    public bool IsActive { get; set; }
    public string? BoundAgentName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
