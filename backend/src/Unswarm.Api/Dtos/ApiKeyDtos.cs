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

public record CreateApiKeyRequest(string Name);

public sealed class ApiKeyCreateResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    [JsonConverter(typeof(CamelCaseEnumJsonConverter))]
    public ApiKeyScope Scope { get; set; }
    public bool IsActive { get; set; }
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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
