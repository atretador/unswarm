namespace Unswarm.Core.Models;

/// <summary>
/// Authorization scope of an API key. The scope decides which protected surface
/// the key is allowed to authenticate (see ApiKeyAuthMiddleware's path→scope map):
/// inference keys reach the OpenAI-compatible proxy (/v1), agent keys reach the
/// remote-agent channel (/api/agents + /ws/agent). Login credentials are a
/// completely separate surface (cookie / /api control plane) and never carry a
/// scope.
/// </summary>
public enum ApiKeyScope
{
    Inference = 0,
    Agent = 1,
}

/// <summary>
/// Read-only view of a managed API key. The raw secret is only ever returned at
/// creation time; every subsequent response carries the hash-derived prefix only.
/// </summary>
public class ApiKeyItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Short human-readable marker (first characters) of the key. Never the full secret.</summary>
    public string KeyPrefix { get; set; } = string.Empty;
    public ApiKeyScope Scope { get; set; } = ApiKeyScope.Inference;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// Returned exactly once, when the key is created. <see cref="Secret"/> must be
/// shown to the user and is never persisted in full — only its hash is stored.
/// </summary>
public sealed class CreateApiKeyResponse : ApiKeyItem
{
    public string Secret { get; set; } = string.Empty;
}
