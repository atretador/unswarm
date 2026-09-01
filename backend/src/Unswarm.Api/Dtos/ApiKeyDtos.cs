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

/// <summary>
/// Per-key model access restrictions. Empty arrays mean unrestricted access.
/// </summary>
public sealed class KeyAccessDto
{
    /// <summary>Allowed cloud provider names (and local runtime display names).</summary>
    public List<string> Providers { get; set; } = [];

    /// <summary>Allowed exact model ids ("cloud/&lt;provider&gt;/&lt;model&gt;" or local model names).</summary>
    public List<string> Models { get; set; } = [];
}

/// <summary>
/// One entry of the provider/model catalog used to configure key access.
/// </summary>
public sealed class ProviderModelCatalogItem
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"cloud" (CloudProviderEntity) or "local" (registered runtime).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Model ids this provider/runtime can serve.</summary>
    public List<string> Models { get; set; } = [];
}

// ── Router Profile DTOs ────────────────────────────────────────────────

public sealed class RouterProfileDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    [JsonConverter(typeof(CamelCaseEnumJsonConverter))]
    public RouterProfileMode Mode { get; set; }
    public List<RouterProfileEntryDto> Entries { get; set; } = [];
    public string? ActiveModelId { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RouterProfileEntryDto
{
    public string ModelId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public record CreateRouterProfileRequest(
    string Name,
    RouterProfileMode Mode = RouterProfileMode.Auto,
    List<RouterProfileEntryDto>? Entries = null);

public record UpdateRouterProfileRequest(
    string Name,
    RouterProfileMode Mode,
    List<RouterProfileEntryDto> Entries);

public sealed class SetActiveEntryRequest
{
    public string? ActiveModelId { get; init; }
}
