namespace Unswarm.Api.Dtos;

// ─── Cloud Provider Management DTOs ──────────────────────────────

public record CreateCloudProviderRequest(
    string Name,
    string BaseUrl,
    string ApiKey,
    int AuthType = 0);

public record UpdateCloudProviderRequest(
    string BaseUrl,
    string? ApiKey = null,   // null/empty = keep existing
    string? ApiKeyHint = null);

public class CloudProviderListItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKeyHint { get; set; } = string.Empty;
    public int ModelCount { get; set; }
    public int AuthType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CloudProviderReadDto : CloudProviderListItemDto
{
    public string BaseUrlFull { get; set; } = string.Empty;
    public string? ChatgptAccountId { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}

public sealed class FetchModelsResultDto
{
    public List<CloudProviderModelMetaDto> Models { get; set; } = [];
}

public sealed class CloudProviderModelListDto
{
    public List<CloudProviderModelMetaDto> Models { get; set; } = [];
}

/// <summary>Model metadata exposed via the API.</summary>
public sealed class CloudProviderModelMetaDto
{
    public string Id { get; set; } = "";
    public int ContextWindow { get; set; }
    public int MaxOutputTokens { get; set; }
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public record TestAndFetchRequest(
    string BaseUrl,
    string ApiKey);

// ─── OAuth Flow DTOs ─────────────────────────────────────────────

public record StartOAuthRequest();

public record PollOAuthRequest(string DeviceAuthId, string UserCode);

public record OAuthStartResultDto(string DeviceAuthId, string UserCode, string VerificationUrl, int Interval);
