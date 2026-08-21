using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public record CreateApiKeyRequest(string Name);

public sealed class ApiKeyCreateResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
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
    public ApiKeyScope Scope { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
