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
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        _logger.LogDebug("Querying container models at {Url}", url);

        try
        {
            using var response = await SharedHttp.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover models on port {Port}", port);
            return [];
        }
    }
}
