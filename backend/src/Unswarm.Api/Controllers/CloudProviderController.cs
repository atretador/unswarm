using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Management of cloud LLM provider registrations. Admin-only.
/// Route: /api/cloudproviders — sits outside ApiKeyAuthMiddleware's protected
/// prefixes so cookie auth + Admin role is the full story.
/// </summary>
/// <remarks>
/// GET /api/cloudproviders — List cloud providers
/// POST /api/cloudproviders — Register a new provider
/// GET /api/cloudproviders/{id} — Get a provider detail
/// PUT /api/cloudproviders/{id} — Update a provider
/// DELETE /api/cloudproviders/{id} — Delete a provider
/// POST /api/cloudproviders/{id}/fetch-models — Fetch models from an upstream provider
/// POST /api/cloudproviders/test-and-fetch — Test connection and preview models
/// PUT /api/cloudproviders/{id}/models — Save selected model list
/// </remarks>
[ApiController]
[Route("api/cloudproviders")]
[Authorize(Roles = "Admin")]
public sealed class CloudProviderController : ControllerBase
{
    private readonly ICloudProviderStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CloudProviderController> _logger;
    private readonly IChatGptOAuthService _oauthService;
    private readonly IApiKeyEncryptor _encryptor;

    /// <summary>Semver version sent to the Codex models endpoint.</summary>
    private const string CodexClientVersion = "0.99.0";

    public CloudProviderController(
        ICloudProviderStore store,
        IHttpClientFactory httpFactory,
        ILogger<CloudProviderController> logger,
        IChatGptOAuthService oauthService,
        IApiKeyEncryptor encryptor)
    {
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
        _oauthService = oauthService;
        _encryptor = encryptor;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await _store.ListAsync(ct);
        return Ok(items.Select(MapToListDto));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var item = await _store.GetAsync(id, ct);
        return item is null
            ? NotFound(new { error = "Cloud provider not found." })
            : Ok(MapToReadDto(item));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCloudProviderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (request.AuthType == 0 && string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(new { error = "API key is required." });

        // Validate name charset: [a-zA-Z0-9-_] — it becomes part of public model ids
        var name = request.Name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9\-_]+$"))
            return BadRequest(new { error = "Name must contain only letters, digits, hyphens, and underscores." });

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl == null)
            return BadRequest(new { error = "Base URL must be a valid absolute URL (origin only, no path)." });

        // Extract hint from key (first 4 + last 4 chars)
        var hint = string.IsNullOrWhiteSpace(request.ApiKey)
            ? "oauth"
            : MaskHint(request.ApiKey.Trim());

        try
        {
            await _store.CreateAsync(name, baseUrl, request.ApiKey?.Trim(), hint, request.AuthType, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        // Return the created item (key masked)
        var created = await _store.GetByNameAsync(name, ct);
        return CreatedAtAction(nameof(Get), new { id = created!.Id }, MapToReadDto(created));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateCloudProviderRequest request, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Cloud provider not found." });

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl == null)
            return BadRequest(new { error = "Base URL must be a valid absolute URL (origin only, no path)." });

        var hint = request.ApiKeyHint;
        if (string.IsNullOrWhiteSpace(hint) && !string.IsNullOrWhiteSpace(request.ApiKey))
            hint = MaskHint(request.ApiKey.Trim());

        try
        {
            await _store.UpdateAsync(id, baseUrl, request.ApiKey, hint, ct);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }

        var updated = await _store.GetAsync(id, ct);
        return Ok(MapToReadDto(updated!));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var deleted = await _store.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Cloud provider not found." });
    }

    [HttpPost("{id}/fetch-models")]
    public async Task<IActionResult> FetchModels(string id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Cloud provider not found." });

        if (existing.AuthType == 1)
        {
            // ChatGPT subscription provider — fetch models via OAuth access token
            var tokenSet = await _store.GetOAuthTokensAsync(id, ct);
            if (tokenSet is null)
                return BadRequest(new { error = "No OAuth tokens found. Please complete the OAuth flow first." });

            string accessToken;
            try
            {
                accessToken = _encryptor.Unprotect(tokenSet.AccessTokenCiphertext);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return BadRequest(new { error = "Unable to decrypt the access token. The encryption key ring may have changed — please re-authenticate." });
            }

            var accountId = tokenSet.ChatgptAccountId;
            var result = await FetchSubscriptionModelsAsync(accessToken, accountId, ct);
            if (result.Error is not null)
                return result.Error;

            await _store.SaveModelsAsync(id, result.ModelIds!, ct);

            _logger.LogInformation("Fetched {Count} models for subscription provider {Id}", result.ModelIds!.Count, id);
            return Ok(new FetchModelsResultDto { ModelIds = result.ModelIds! });
        }

        // API key provider — existing behavior
        string? apiKey;
        try
        {
            apiKey = await _store.GetApiKeyAsync(id, ct);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return BadRequest(new { error = "Unable to decrypt the API key for this provider. The encryption key ring may have changed — please re-enter the API key." });
        }

        if (apiKey == null)
            return StatusCode(500, new { error = "Provider key is unavailable." });

        var baseUrl = NormalizeBaseUrl(existing.BaseUrlFull) ?? existing.BaseUrlFull;
        var apiKeyResult = await FetchUpstreamModelsAsync(baseUrl, apiKey, ct);
        if (apiKeyResult.Error is not null)
            return apiKeyResult.Error;

        // Save models to DB
        await _store.SaveModelsAsync(id, apiKeyResult.ModelIds!, ct);

        _logger.LogInformation("Fetched {Count} models for provider {Id}", apiKeyResult.ModelIds!.Count, id);
        return Ok(new FetchModelsResultDto { ModelIds = apiKeyResult.ModelIds! });
    }

    /// <summary>
    /// Test a connection and fetch models without saving. Used by the Add Provider
    /// dialog to preview models before committing.
    /// </summary>
    [HttpPost("test-and-fetch")]
    public async Task<IActionResult> TestAndFetch([FromBody] TestAndFetchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
            return BadRequest(new { error = "Base URL is required." });
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(new { error = "API key is required." });

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl == null)
            return BadRequest(new { error = "Base URL is not valid." });

        var result = await FetchUpstreamModelsAsync(baseUrl, request.ApiKey.Trim(), ct);
        if (result.Error is not null)
            return result.Error;

        return Ok(new FetchModelsResultDto { ModelIds = result.ModelIds! });
    }

    [HttpPut("{id}/models")]
    public async Task<IActionResult> SaveModels(string id, [FromBody] CloudProviderModelListDto request, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Cloud provider not found." });

        try
        {
            await _store.SaveModelsAsync(id, request.ModelIds, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var updated = await _store.GetAsync(id, ct);
        return Ok(MapToReadDto(updated!));
    }

    // ── OAuth Endpoints ──────────────────────────────────────────

    [HttpPost("{id}/oauth/start")]
    public async Task<IActionResult> StartOAuth(string id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Cloud provider not found." });
        if (existing.AuthType != 1)
            return BadRequest(new { error = "OAuth is only available for ChatGPT subscription providers (authType == 1)." });

        try
        {
            var result = await _oauthService.StartDeviceCodeFlowAsync(ct);
            return Ok(new OAuthStartResultDto(result.DeviceAuthId, result.UserCode, result.VerificationUrl, result.Interval));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start OAuth device code flow for provider {Id}", id);
            return StatusCode(502, new { error = "Failed to initiate OAuth flow with upstream provider." });
        }
    }

    [HttpPost("{id}/oauth/poll")]
    public async Task<IActionResult> PollOAuth(string id, [FromBody] PollOAuthRequest request, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Cloud provider not found." });
        if (existing.AuthType != 1)
            return BadRequest(new { error = "OAuth is only available for ChatGPT subscription providers (authType == 1)." });

        try
        {
            // If tokens are already saved (e.g. from a prior successful poll), return success immediately
            if (existing.ChatgptAccountId is not null && existing.TokenExpiresAt is not null)
            {
                _logger.LogInformation("OAuth already completed for provider {Id}, accountId={AccountId}", id, existing.ChatgptAccountId);
                return Ok(new { status = "success", chatgptAccountId = existing.ChatgptAccountId });
            }

            var tokenResult = await _oauthService.PollForTokenAsync(request.DeviceAuthId, request.UserCode, ct);

            if (tokenResult is null)
                return Ok(new { status = "pending", message = "Authorization pending. Please complete the sign-in." });

            // Encrypt and save tokens
            var accessTokenCipher = _encryptor.Protect(tokenResult.AccessToken);
            var refreshTokenCipher = _encryptor.Protect(tokenResult.RefreshToken);
            await _store.SaveOAuthTokensAsync(id, accessTokenCipher, refreshTokenCipher, tokenResult.ExpiresAt, tokenResult.ChatgptAccountId, ct);

            _logger.LogInformation("OAuth tokens saved for provider {Id}, accountId={AccountId}", id, tokenResult.ChatgptAccountId);
            return Ok(new { status = "success", chatgptAccountId = tokenResult.ChatgptAccountId });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("OAuth poll error for provider {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to poll OAuth tokens for provider {Id}", id);
            return StatusCode(502, new { error = "Failed to poll OAuth tokens from upstream provider." });
        }
    }

    [HttpPost("{id}/oauth/refresh")]
    public async Task<IActionResult> RefreshOAuth(string id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Cloud provider not found." });
        if (existing.AuthType != 1)
            return BadRequest(new { error = "OAuth is only available for ChatGPT subscription providers (authType == 1)." });

        var tokenSet = await _store.GetOAuthTokensAsync(id, ct);
        if (tokenSet is null)
            return BadRequest(new { error = "No OAuth tokens found. Please complete the OAuth flow first." });

        string refreshToken;
        try
        {
            refreshToken = _encryptor.Unprotect(tokenSet.RefreshTokenCiphertext);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return BadRequest(new { error = "Unable to decrypt the refresh token. The encryption key ring may have changed — please re-authenticate." });
        }

        try
        {
            var tokenResult = await _oauthService.RefreshTokenAsync(refreshToken, ct);

            var accessTokenCipher = _encryptor.Protect(tokenResult.AccessToken);
            var refreshTokenCipher = _encryptor.Protect(tokenResult.RefreshToken);
            await _store.SaveOAuthTokensAsync(id, accessTokenCipher, refreshTokenCipher, tokenResult.ExpiresAt, tokenResult.ChatgptAccountId, ct);

            _logger.LogInformation("OAuth tokens refreshed for provider {Id}", id);
            return Ok(new { status = "success", chatgptAccountId = tokenResult.ChatgptAccountId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh OAuth tokens for provider {Id}", id);
            return StatusCode(502, new { error = "Failed to refresh OAuth tokens from upstream provider." });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<(List<string>? ModelIds, IActionResult? Error)> FetchUpstreamModelsAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var httpClient = _httpFactory.CreateClient("cloud-provider");
        var modelsUrl = $"{baseUrl.TrimEnd('/')}/models";

        _logger.LogInformation("Fetching models from {Url}", modelsUrl);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Fetch models returned {Status} from {Url}: {Body}", response.StatusCode, modelsUrl, errorBody);
                return (null, StatusCode((int)response.StatusCode, new { error = $"Upstream returned {response.StatusCode}" }));
            }

            var modelsResponse = await response.Content.ReadFromJsonAsync<OpenAiModelListResponse>(ct)
                ?? throw new InvalidOperationException("Upstream did not return a valid model list.");

            var modelIds = modelsResponse.Data
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            return (modelIds, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Transport error fetching models from {Url}", modelsUrl);
            return (null, StatusCode(502, new { error = "Failed to connect to provider." }));
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout fetching models from {Url}", modelsUrl);
            return (null, StatusCode(504, new { error = "Upstream request timed out." }));
        }
    }

    private async Task<(List<string>? ModelIds, IActionResult? Error)> FetchSubscriptionModelsAsync(
        string accessToken, string? accountId, CancellationToken ct)
    {
        var httpClient = _httpFactory.CreateClient("cloud-provider");
        var modelsUrl = $"https://chatgpt.com/backend-api/codex/models?client_version={CodexClientVersion}";

        _logger.LogInformation("Fetching subscription models from {Url}", modelsUrl);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            if (!string.IsNullOrWhiteSpace(accountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", accountId);
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
            request.Headers.TryAddWithoutValidation("originator", "unswarm");

            var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Fetch subscription models returned {Status} from {Url}: {Body}", response.StatusCode, modelsUrl, errorBody);
                return (null, StatusCode((int)response.StatusCode, new { error = $"Upstream returned {response.StatusCode}" }));
            }

            var modelsResponse = await response.Content.ReadFromJsonAsync<CodexModelsResponse>(ct)
                ?? throw new InvalidOperationException("Upstream did not return a valid model list.");

            var modelIds = modelsResponse.Models
                .Where(m => m.SupportedInApi && !string.IsNullOrWhiteSpace(m.Slug))
                .Select(m => m.Slug)
                .Distinct()
                .ToList();

            return (modelIds, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Transport error fetching subscription models from {Url}", modelsUrl);
            return (null, StatusCode(502, new { error = "Failed to connect to provider." }));
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout fetching subscription models from {Url}", modelsUrl);
            return (null, StatusCode(504, new { error = "Upstream request timed out." }));
        }
    }

    private static string? NormalizeBaseUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var url = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;

        var result = uri.ToString().TrimEnd('/');

        // If origin-only (no path), default to /v1 — most OpenAI-compatible APIs
        if (uri.AbsolutePath is "/" or "")
            result += "/v1";

        return result;
    }

    /// <summary>
    /// Create a masked hint from an API key (e.g. "sk-abcdef3f9a").
    /// Shows first 8 chars + "…" + last 4 chars.
    /// </summary>
    private static string MaskHint(string key)
    {
        if (key.Length <= 16)
            return key[..4] + "…" + key[^4..];
        return key[..8] + "…" + key[^4..];
    }

    private static CloudProviderListItemDto MapToListDto(Unswarm.Core.Contracts.CloudProviderListItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        BaseUrl = item.BaseUrl,
        ApiKeyHint = item.ApiKeyHint,
        ModelCount = item.ModelCount,
        AuthType = item.AuthType,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };

    private static CloudProviderReadDto MapToReadDto(Unswarm.Core.Contracts.CloudProviderReadItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        BaseUrl = item.BaseUrl,
        BaseUrlFull = item.BaseUrlFull,
        ApiKeyHint = item.ApiKeyHint,
        ModelCount = item.ModelCount,
        AuthType = item.AuthType,
        ChatgptAccountId = item.ChatgptAccountId,
        TokenExpiresAt = item.TokenExpiresAt,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };
}
