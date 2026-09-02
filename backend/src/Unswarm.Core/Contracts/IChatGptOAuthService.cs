namespace Unswarm.Core.Contracts;

public record DeviceCodeResult(
    string DeviceAuthId,
    string UserCode,
    string VerificationUrl,
    int Interval);

public record OAuthTokenResult(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? ChatgptAccountId);

public interface IChatGptOAuthService
{
    /// <summary>Initiate proprietary device code flow. Returns user code + device auth id.</summary>
    Task<DeviceCodeResult> StartDeviceCodeFlowAsync(CancellationToken ct);

    /// <summary>Poll for token after user authenticates. Returns null if still pending.</summary>
    Task<OAuthTokenResult?> PollForTokenAsync(string deviceAuthId, string userCode, CancellationToken ct);

    /// <summary>Refresh an existing access token using refresh token.</summary>
    Task<OAuthTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken ct);
}
