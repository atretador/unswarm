using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;

namespace Unswarm.Core.Services;

/// <summary>
/// Implements the proprietary ChatGPT OAuth device code flow used by the Codex CLI.
/// A 3-step process: get user code, poll for authorization, exchange code for tokens.
/// </summary>
public sealed class ChatGptOAuthService : IChatGptOAuthService
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string UserCodeEndpoint = "https://auth.openai.com/api/accounts/deviceauth/usercode";
    private const string PollEndpoint = "https://auth.openai.com/api/accounts/deviceauth/token";
    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    private const string RedirectUri = "https://auth.openai.com/deviceauth/callback";
    private const string AuthNamespace = "https://api.openai.com/auth";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChatGptOAuthService> _logger;

    public ChatGptOAuthService(
        IHttpClientFactory httpClientFactory,
        ILogger<ChatGptOAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<DeviceCodeResult> StartDeviceCodeFlowAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("chatgpt-oauth");

        var payload = new { client_id = ClientId };

        var response = await client.PostAsJsonAsync(UserCodeEndpoint, payload, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        var result = new DeviceCodeResult(
            DeviceAuthId: json.GetProperty("device_auth_id").GetString()!,
            UserCode: json.GetProperty("user_code").GetString()!,
            VerificationUrl: "https://auth.openai.com/codex/device",
            Interval: json.TryGetProperty("interval", out var intervalProp)
                ? (intervalProp.ValueKind == JsonValueKind.String
                    ? int.Parse(intervalProp.GetString()!)
                    : intervalProp.GetInt32())
                : 5);

        _logger.LogInformation("Device code flow initiated. User code: {UserCode}", result.UserCode);
        return result;
    }

    public async Task<OAuthTokenResult?> PollForTokenAsync(string deviceAuthId, string userCode, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("chatgpt-oauth");

        var payload = new { device_auth_id = deviceAuthId, user_code = userCode };

        var response = await client.PostAsJsonAsync(PollEndpoint, payload, ct);

        // Pending: 404 or 403 means user hasn't authorized yet
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            return null;

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        // Extract the authorization code, challenge, and verifier from the poll response
        var authorizationCode = json.GetProperty("authorization_code").GetString()!;
        var codeChallenge = json.GetProperty("code_challenge").GetString()!;
        var codeVerifier = json.GetProperty("code_verifier").GetString()!;

        // Exchange the authorization code for tokens using form-urlencoded POST
        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = codeVerifier
        });

        var tokenResponse = await client.PostAsync(TokenEndpoint, formContent, ct);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            _logger.LogError("OAuth token exchange failed with {StatusCode}: {Body}", tokenResponse.StatusCode, tokenBody);
            throw new InvalidOperationException($"OAuth token exchange failed ({tokenResponse.StatusCode}): {tokenBody}");
        }

        var tokenJson = JsonDocument.Parse(tokenBody).RootElement;

        if (tokenJson.TryGetProperty("error", out var errorProp))
        {
            var error = errorProp.GetString();
            var errorDesc = tokenJson.TryGetProperty("error_description", out var descProp)
                ? descProp.GetString()
                : error;
            throw new InvalidOperationException($"OAuth token exchange failed: {errorDesc}");
        }

        return ParseTokenResponse(tokenJson);
    }

    public async Task<OAuthTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("chatgpt-oauth");

        var payload = new
        {
            client_id = ClientId,
            grant_type = "refresh_token",
            refresh_token = refreshToken
        };

        var response = await client.PostAsJsonAsync(TokenEndpoint, payload, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        if (json.TryGetProperty("error", out var errorProp))
        {
            var error = errorProp.GetString();
            var errorDesc = json.TryGetProperty("error_description", out var descProp)
                ? descProp.GetString()
                : error;
            throw new InvalidOperationException($"OAuth token refresh failed: {errorDesc}");
        }

        return ParseTokenResponse(json);
    }

    /// <summary>
    /// Parse the OAuth token response and extract the ChatGPT account ID from the access token JWT.
    /// </summary>
    private OAuthTokenResult ParseTokenResponse(JsonElement json)
    {
        var accessToken = json.GetProperty("access_token").GetString()!;
        var refreshToken = json.GetProperty("refresh_token").GetString()!;

        // The proprietary flow may return id_token with expiry info; fall back to expires_in if present
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1); // default 1h
        if (json.TryGetProperty("expires_in", out var expiresInProp))
        {
            var expiresIn = expiresInProp.GetInt32();
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        }

        // Try to extract account ID from id_token if present
        string? chatgptAccountId = null;
        if (json.TryGetProperty("id_token", out var idTokenProp))
        {
            var idToken = idTokenProp.GetString()!;
            chatgptAccountId = ExtractAccountIdFromJwt(idToken);
        }

        // Fallback: try access_token
        if (chatgptAccountId is null)
            chatgptAccountId = ExtractAccountIdFromJwt(accessToken);

        return new OAuthTokenResult(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: expiresAt,
            ChatgptAccountId: chatgptAccountId);
    }

    /// <summary>
    /// Decode the JWT payload (without signature verification) and extract the
    /// ChatGPT account ID from the OpenAI auth namespace claims.
    /// </summary>
    private static string? ExtractAccountIdFromJwt(string jwt)
    {
        try
        {
            // JWT format: header.payload.signature
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            // Base64url decode the payload
            var payload = parts[1];
            // Pad for standard base64
            var padded = payload.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            var bytes = Convert.FromBase64String(padded);
            var json = Encoding.UTF8.GetString(bytes);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Look for the OpenAI auth namespace claim
            if (root.TryGetProperty(AuthNamespace, out var namespaceClaims))
            {
                // The namespace claim is an object with chatgpt_account_id
                if (namespaceClaims.TryGetProperty("chatgpt_account_id", out var accountId))
                    return accountId.GetString();
            }

            // Fallback: look for a direct chatgpt_account_id claim at the top level
            if (root.TryGetProperty("chatgpt_account_id", out var directAccountId))
                return directAccountId.GetString();

            return null;
        }
        catch
        {
            // JWT parsing failed — return null rather than throwing
            return null;
        }
    }
}
