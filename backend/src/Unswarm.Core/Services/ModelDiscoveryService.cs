using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

/// <summary>
/// Queries a container's /v1/models endpoint and parses the OpenAI-compatible response.
/// </summary>
public sealed class ModelDiscoveryService
{
    private readonly ILogger<ModelDiscoveryService> _logger;
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public ModelDiscoveryService(ILogger<ModelDiscoveryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Queries http://127.0.0.1:{port}/v1/models and returns the list of discovered models.
    /// Expects an OpenAI-compatible response: { "data": [{ "id": "...", "owned_by": "..." }] }
    ///
    /// TRANSPORT FAILURES THROW: connection refused, timeouts, and non-2xx responses
    /// propagate to the caller so a dead container surfaces as a registration/rediscover
    /// error instead of silently reporting zero models. Only response-shape issues
    /// (missing "data" array, malformed JSON, entries without an id) return an empty list.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        _logger.LogDebug("Querying container models at {Url}", url);

        JsonElement json;
        try
        {
            using var response = await SharedHttp.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Malformed JSON body from a live endpoint → treat as "no models" (shape issue).
            _logger.LogWarning("Malformed /v1/models response from port {Port}", port);
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Transport failures (connection refused, timeout) and non-2xx statuses
            // are NOT swallowed: callers (registration/rediscovery) must see them as
            // a dead container, not as zero models.
            _logger.LogError(ex, "Failed to discover models on port {Port}", port);
            throw;
        }

        if (!json.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("No 'data' array in /v1/models response from port {Port}", port);
            return [];
        }

        var models = new List<DiscoveredModel>();
        foreach (var item in dataArray.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                _logger.LogWarning("Skipping /v1/models entry with missing id");
                continue;
            }

            var ownedBy = item.TryGetProperty("owned_by", out var obProp) ? obProp.GetString() : null;

            models.Add(new DiscoveredModel
            {
                ModelId = id,
                OwnedBy = ownedBy
            });
        }

        _logger.LogInformation("Discovered {Count} models on port {Port}", models.Count, port);
        return models;
    }
}
