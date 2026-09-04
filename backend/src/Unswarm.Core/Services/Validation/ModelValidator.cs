using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services.Validation;

public sealed class ModelValidator
{
    private readonly ILogger<ModelValidator> _logger;
    private readonly string _host;

    public ModelValidator(ILogger<ModelValidator> logger, IOptions<ContainerHostOptions> options)
    {
        _logger = logger;
        _host = options.Value.Host;
    }

    /// <summary>
    /// Validates a model endpoint: TCP connect → /health → /v1/models identity → smoke inference.
    /// </summary>
    public async Task<ModelValidationResult> ValidateAsync(int port, string expectedModelName, CancellationToken ct = default)
    {
        // Step 1: TCP connectivity
        _logger.LogDebug("Validating TCP connectivity on port {Port}", port);
        if (!await TcpCheckAsync(port, ct).ConfigureAwait(false))
        {
            return ModelValidationResult.Fail("TCP connect failed");
        }

        // Step 2: /health endpoint
        _logger.LogDebug("Checking /health endpoint on port {Port}", port);
        if (!await HealthCheckAsync(port, ct).ConfigureAwait(false))
        {
            return ModelValidationResult.Fail("/health endpoint not responding");
        }

        // Step 3: /v1/models identity
        _logger.LogDebug("Checking /v1/models identity on port {Port}", port);
        if (!await IdentityCheckAsync(port, expectedModelName, ct).ConfigureAwait(false))
        {
            return ModelValidationResult.Fail("/v1/models identity mismatch");
        }

        // Step 4: Smoke inference
        _logger.LogDebug("Running smoke inference on port {Port}", port);
        if (!await SmokeInferenceAsync(port, ct).ConfigureAwait(false))
        {
            return ModelValidationResult.Fail("Smoke inference failed");
        }

        return ModelValidationResult.Success();
    }

    private async Task<bool> TcpCheckAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, port, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> HealthCheckAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"http://{_host}:{port}/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IdentityCheckAsync(int port, string expectedModelName, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetStringAsync($"http://{_host}:{port}/v1/models", ct).ConfigureAwait(false);
            // Simple check: response should contain the model name
            return response.Contains(expectedModelName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SmokeInferenceAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var payload = """{"model":"test","messages":[{"role":"user","content":"hi"}],"max_tokens":1}""";
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await http.PostAsync($"http://{_host}:{port}/v1/chat/completions", content, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ModelValidationResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }

    public static ModelValidationResult Success() => new() { IsSuccess = true };
    public static ModelValidationResult Fail(string message) => new() { IsSuccess = false, ErrorMessage = message };
}
