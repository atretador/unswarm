using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Helpers;
using Unswarm.Core.Models;
using Unswarm.Core.Services;

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
[Authorize(Policy = "ControlPlaneAccess")]
public sealed class CloudProviderController : ControllerBase
{
    private readonly ICloudProviderStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CloudProviderController> _logger;
    private readonly IChatGptOAuthService _oauthService;
    private readonly IApiKeyEncryptor _encryptor;
    private readonly bool _allowPrivateEgress;
    private readonly string[] _allowedPrivateHosts;

    /// <summary>Semver version sent to the Codex models endpoint.</summary>
    private const string CodexClientVersion = "0.99.0";

    public CloudProviderController(
        ICloudProviderStore store,
        IHttpClientFactory httpFactory,
        ILogger<CloudProviderController> logger,
        IChatGptOAuthService oauthService,
        IApiKeyEncryptor encryptor,
        IConfiguration configuration)
    {
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
        _oauthService = oauthService;
        _encryptor = encryptor;
        _allowPrivateEgress = configuration.GetValue<bool>("CloudProviders:AllowPrivateEgress");
        _allowedPrivateHosts = configuration.GetSection("CloudProviders:AllowedPrivateHosts").Get<string[]>() ?? [];
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
            ? NotFound(LocalizedError.Create("cloudProviders.notFound"))
            : Ok(MapToReadDto(item));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCloudProviderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(LocalizedError.Create("cloudProviders.nameRequired"));
        if (request.AuthType == 0 && string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(LocalizedError.Create("cloudProviders.apiKeyRequired"));

        // Validate name charset: [a-zA-Z0-9-_] — it becomes part of public model ids
        var name = request.Name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9\-_]+$"))
            return BadRequest(LocalizedError.Create("cloudProviders.nameInvalidChars"));

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl == null)
            return BadRequest(LocalizedError.Create("cloudProviders.baseUrlInvalid"));

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
            return NotFound(LocalizedError.Create("cloudProviders.notFound"));

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl == null)
            return BadRequest(LocalizedError.Create("cloudProviders.baseUrlInvalid"));

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
        return deleted ? NoContent() : NotFound(LocalizedError.Create("cloudProviders.notFound"));
    }

    [HttpPost("{id}/fetch-models")]
    public async Task<IActionResult> FetchModels(string id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(LocalizedError.Create("cloudProviders.notFound"));

        if (existing.AuthType == 1)
        {
            // ChatGPT subscription provider — fetch models via OAuth access token
            var tokenSet = await _store.GetOAuthTokensAsync(id, ct);
            if (tokenSet is null)
                return BadRequest(LocalizedError.Create("cloudProviders.noOAuthTokens"));

            string accessToken;
            try
            {
                accessToken = _encryptor.Unprotect(tokenSet.AccessTokenCiphertext);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return BadRequest(LocalizedError.Create("cloudProviders.cannotDecryptAccessToken"));
            }

            var accountId = tokenSet.ChatgptAccountId;
            var result = await FetchSubscriptionModelsAsync(accessToken, accountId, ct);
            if (result.Error is not null)
                return result.Error;

            await _store.SaveModelsAsync(id, result.Models!, ct);

            _logger.LogInformation("Fetched {Count} models for subscription provider {Id}", result.Models!.Count, id);
            return Ok(new FetchModelsResultDto { Models = result.Models!.Select(MapToDto).ToList() });
        }

        // API key provider — existing behavior
        string? apiKey;
        try
        {
            apiKey = await _store.GetApiKeyAsync(id, ct);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return BadRequest(LocalizedError.Create("cloudProviders.cannotDecryptApiKey"));
        }

        if (apiKey == null)
            return StatusCode(500, LocalizedError.Create("cloudProviders.keyUnavailable"));

        var baseUrl = NormalizeBaseUrl(existing.BaseUrlFull) ?? existing.BaseUrlFull;
        var apiKeyResult = await FetchUpstreamModelsAsync(baseUrl, apiKey, ct);
        if (apiKeyResult.Error is not null)
            return apiKeyResult.Error;

        // Save models to DB
        await _store.SaveModelsAsync(id, apiKeyResult.Models!, ct);

        _logger.LogInformation("Fetched {Count} models for provider {Id}", apiKeyResult.Models!.Count, id);
        return Ok(new FetchModelsResultDto { Models = apiKeyResult.Models!.Select(MapToDto).ToList() });
    }

    /// <summary>
    /// Test a connection and fetch models without saving. Used by the Add Provider
    /// dialog to preview models before committing.
    /// </summary>
    [HttpPost("test-and-fetch")]
    public async Task<IActionResult> TestAndFetch([FromBody] TestAndFetchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
            return BadRequest(LocalizedError.Create("cloudProviders.baseUrlRequired"));
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(LocalizedError.Create("cloudProviders.apiKeyRequired"));

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl == null)
            return BadRequest(LocalizedError.Create("cloudProviders.baseUrlInvalid"));

        var result = await FetchUpstreamModelsAsync(baseUrl, request.ApiKey.Trim(), ct);
        if (result.Error is not null)
            return result.Error;

        return Ok(new FetchModelsResultDto { Models = result.Models!.Select(MapToDto).ToList() });
    }

    [HttpPut("{id}/models")]
    public async Task<IActionResult> SaveModels(string id, [FromBody] CloudProviderModelListDto request, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(LocalizedError.Create("cloudProviders.notFound"));

        // Validate before mapping: explicit unknown tokens are a client error,
        // matching the local model create/update contract. A null/omitted list is
        // allowed and means "keep the stored selection" (see merge below).
        foreach (var dto in request.Models)
        {
            if (ModelModalities.FindUnknownToken(dto.InputModalities) is { } badModality)
                return BadRequest(LocalizedError.Create("models.inputModalitiesInvalid", new { token = badModality }));
        }

        try
        {
            // Merge with stored metadata so an omitted inputModalities preserves the
            // admin's existing selection; only explicitly provided lists overwrite.
            var storedById = new Dictionary<string, CloudProviderModelMeta>(StringComparer.Ordinal);
            foreach (var stored in await _store.GetModelMetasAsync(id, ct).ConfigureAwait(false))
                storedById[stored.Id] = stored;

            var metas = request.Models.Select(d => MapFromDto(d, storedById)).ToList();
            await _store.SaveModelsAsync(id, metas, ct);
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
            return NotFound(LocalizedError.Create("cloudProviders.notFound"));
        if (existing.AuthType != 1)
            return BadRequest(LocalizedError.Create("cloudProviders.oauthNotAvailable"));

        try
        {
            var result = await _oauthService.StartDeviceCodeFlowAsync(ct);
            return Ok(new OAuthStartResultDto(result.DeviceAuthId, result.UserCode, result.VerificationUrl, result.Interval));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start OAuth device code flow for provider {Id}", id);
            return StatusCode(502, LocalizedError.Create("cloudProviders.oauthStartFailed"));
        }
    }

    [HttpPost("{id}/oauth/poll")]
    public async Task<IActionResult> PollOAuth(string id, [FromBody] PollOAuthRequest request, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(LocalizedError.Create("cloudProviders.notFound"));
        if (existing.AuthType != 1)
            return BadRequest(LocalizedError.Create("cloudProviders.oauthNotAvailable"));

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
            return StatusCode(502, LocalizedError.Create("cloudProviders.oauthPollFailed"));
        }
    }

    [HttpPost("{id}/oauth/refresh")]
    public async Task<IActionResult> RefreshOAuth(string id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return NotFound(LocalizedError.Create("cloudProviders.notFound"));
        if (existing.AuthType != 1)
            return BadRequest(LocalizedError.Create("cloudProviders.oauthNotAvailable"));

        var tokenSet = await _store.GetOAuthTokensAsync(id, ct);
        if (tokenSet is null)
            return BadRequest(LocalizedError.Create("cloudProviders.noOAuthTokens"));

        string refreshToken;
        try
        {
            refreshToken = _encryptor.Unprotect(tokenSet.RefreshTokenCiphertext);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return BadRequest(LocalizedError.Create("cloudProviders.cannotDecryptRefreshToken"));
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
            return StatusCode(502, LocalizedError.Create("cloudProviders.oauthRefreshFailed"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<(List<CloudProviderModelMeta>? Models, IActionResult? Error)> FetchUpstreamModelsAsync(
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
                return (null, StatusCode((int)response.StatusCode, LocalizedError.Create("cloudProviders.upstreamError", new { statusCode = (int)response.StatusCode })));
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var models = ParseUpstreamModels(responseBody);
            return (models, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Transport error fetching models from {Url}", modelsUrl);
            return (null, StatusCode(502, LocalizedError.Create("cloudProviders.connectFailed")));
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout fetching models from {Url}", modelsUrl);
            return (null, StatusCode(504, LocalizedError.Create("cloudProviders.requestTimeout")));
        }
    }

    private async Task<(List<CloudProviderModelMeta>? Models, IActionResult? Error)> FetchSubscriptionModelsAsync(
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
                return (null, StatusCode((int)response.StatusCode, LocalizedError.Create("cloudProviders.upstreamError", new { statusCode = (int)response.StatusCode })));
            }

            var modelsResponse = await response.Content.ReadFromJsonAsync<CodexModelsResponse>(ct)
                ?? throw new InvalidOperationException("Upstream did not return a valid model list.");

            var models = modelsResponse.Models
                .Where(m => m.SupportedInApi && !string.IsNullOrWhiteSpace(m.Slug))
                .Select(m => CloudProviderModelMeta.FromId(m.Slug))
                .DistinctBy(m => m.Id)
                .ToList();

            return (models, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Transport error fetching subscription models from {Url}", modelsUrl);
            return (null, StatusCode(502, LocalizedError.Create("cloudProviders.connectFailed")));
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout fetching subscription models from {Url}", modelsUrl);
            return (null, StatusCode(504, LocalizedError.Create("cloudProviders.requestTimeout")));
        }
    }

    /// <summary>
    /// Parse upstream /v1/models response and extract model metadata.
    /// Supports OpenAI, OpenRouter (context_length, top_provider), vLLM (max_model_len), Ollama (context_length).
    /// </summary>
    private List<CloudProviderModelMeta> ParseUpstreamModels(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
                return [];

            var models = new List<CloudProviderModelMeta>();
            foreach (var item in dataArray.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var meta = CloudProviderModelMeta.FromId(id);

                // Display name
                if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    meta.DisplayName = nameProp.GetString() ?? "";

                // Context length: try multiple field names used by different providers
                if (TryGetInt(item, "context_length", out var ctxLen))
                    meta.ContextWindow = ctxLen;
                else if (TryGetInt(item, "max_model_len", out var mml))
                    meta.ContextWindow = mml;

                // Max output tokens: try top_provider.max_completion_tokens (OpenRouter)
                if (item.TryGetProperty("top_provider", out var topProvider) && topProvider.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetInt(topProvider, "max_completion_tokens", out var maxOut))
                        meta.MaxOutputTokens = maxOut;
                    if (meta.ContextWindow == 0 && TryGetInt(topProvider, "context_length", out var tpCtx))
                        meta.ContextWindow = tpCtx;
                }

                // Input modalities: OpenRouter exposes architecture.input_modalities
                // (array of tokens) or architecture.modality ("text+image->text").
                if (item.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object)
                {
                    if (architecture.TryGetProperty("input_modalities", out var inputModalities)
                        && inputModalities.ValueKind == JsonValueKind.Array)
                    {
                        var tokens = inputModalities.EnumerateArray()
                            .Where(t => t.ValueKind == JsonValueKind.String)
                            .Select(t => t.GetString() ?? "");
                        meta.InputModalities = ModelModalities.Normalize(tokens);
                    }
                    else if (architecture.TryGetProperty("modality", out var modality)
                        && modality.ValueKind == JsonValueKind.String)
                    {
                        meta.InputModalities = ModelModalities.ParseModalityString(modality.GetString());
                    }
                }

                models.Add(meta);
            }

            _logger.LogInformation("Parsed {Count} models with metadata from upstream", models.Count);
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse upstream model metadata, falling back to bare IDs");
            return ParseBareIds(responseBody);
        }
    }

    /// <summary>Fallback: parse just model IDs from a response we couldn't enrich.</summary>
    private List<CloudProviderModelMeta> ParseBareIds(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
                return [];

            return dataArray.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "")
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Select(CloudProviderModelMeta.FromId)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;
        if (prop.ValueKind == JsonValueKind.Number)
        {
            value = prop.GetInt32();
            return value > 0;
        }
        return false;
    }

    private string? NormalizeBaseUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var url = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;

        // Early reject literal private/reserved IPs unless the operator has opted
        // in (AllowPrivateEgress) or allow-listed the host. The ConnectCallback
        // remains the authoritative filter (it re-checks every DNS-resolved
        // address on connect/reconnect).
        if (!_allowPrivateEgress
            && !NetworkAddressPolicy.MatchesAllowedHost(uri.DnsSafeHost, _allowedPrivateHosts)
            && IPAddress.TryParse(uri.DnsSafeHost, out var literal)
            && NetworkAddressPolicy.IsPrivateOrReserved(literal))
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

    private static CloudProviderModelMetaDto MapToDto(CloudProviderModelMeta m) => new()
    {
        Id = m.Id,
        ContextWindow = m.ContextWindow,
        MaxOutputTokens = m.MaxOutputTokens,
        Family = m.Family,
        ParameterSize = m.ParameterSize,
        Quantization = m.Quantization,
        DisplayName = m.DisplayName,
        InputModalities = ModelModalities.Normalize(m.InputModalities),
    };

    private static CloudProviderModelMeta MapFromDto(
        CloudProviderModelMetaDto d,
        IReadOnlyDictionary<string, CloudProviderModelMeta> storedById)
    {
        // Explicit list wins; omitted (null) preserves the stored selection, and a
        // brand-new model defaults to text-only.
        var modalities = d.InputModalities is not null
            ? ModelModalities.Normalize(d.InputModalities)
            : storedById.TryGetValue(d.Id, out var stored)
                ? ModelModalities.Normalize(stored.InputModalities)
                : ModelModalities.TextOnly();

        return new()
        {
            Id = d.Id,
            ContextWindow = d.ContextWindow,
            MaxOutputTokens = d.MaxOutputTokens,
            Family = d.Family,
            ParameterSize = d.ParameterSize,
            Quantization = d.Quantization,
            DisplayName = d.DisplayName,
            InputModalities = modalities,
        };
    }
}
