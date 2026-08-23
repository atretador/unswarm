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
[ApiController]
[Route("api/cloudproviders")]
[Authorize(Roles = "Admin")]
public sealed class CloudProviderController : ControllerBase
{
    private readonly ICloudProviderStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CloudProviderController> _logger;

    public CloudProviderController(
        ICloudProviderStore store,
        IHttpClientFactory httpFactory,
        ILogger<CloudProviderController> logger)
    {
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
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
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(new { error = "API key is required." });

        // Validate name charset: [a-zA-Z0-9-_] — it becomes part of public model ids
        var name = request.Name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9\-_]+$"))
            return BadRequest(new { error = "Name must contain only letters, digits, hyphens, and underscores." });

        var baseUrl = NormalizeBaseUrl(request.BaseUrl);
        if (baseUrl == null)
            return BadRequest(new { error = "Base URL must be a valid absolute URL (origin only, no path)." });

        // Extract hint from key (first 4 + last 4 chars)
        var hint = MaskHint(request.ApiKey.Trim());

        try
        {
            await _store.CreateAsync(name, baseUrl, request.ApiKey.Trim(), hint, ct);
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

        var apiKey = await _store.GetApiKeyAsync(id, ct);
        if (apiKey == null)
            return StatusCode(500, new { error = "Provider key is unavailable." });

        var baseUrl = NormalizeBaseUrl(existing.BaseUrlFull) ?? existing.BaseUrlFull;
        var result = await FetchUpstreamModelsAsync(baseUrl, apiKey, ct);
        if (result.Error is not null)
            return result.Error;

        // Save models to DB
        await _store.SaveModelsAsync(id, result.ModelIds!, ct);

        _logger.LogInformation("Fetched {Count} models for provider {Id}", result.ModelIds!.Count, id);
        return Ok(new FetchModelsResultDto { ModelIds = result.ModelIds! });
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
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };
}
