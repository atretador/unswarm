using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Unswarm.Api.Configuration;

namespace Unswarm.Api.Middleware;

public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthOptions _options;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, IOptions<AuthOptions> options, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var isProtected = false;
        foreach (var prefix in _options.ProtectedPaths)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                isProtected = true;
                break;
            }
        }

        if (!isProtected)
        {
            await _next(context);
            return;
        }

        string? key = null;

        if (context.Request.Headers.TryGetValue("X-Api-Key", out var xApiKey) &&
            !string.IsNullOrWhiteSpace(xApiKey))
        {
            key = xApiKey.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var authStr = authHeader.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                key = authStr[7..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(key) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(key),
                Encoding.UTF8.GetBytes(_options.ApiKey)))
        {
            _logger.LogWarning("Auth failed for {Path} from {Ip}",
                context.Request.Path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Unauthorized" }));
            return;
        }

        await _next(context);
    }
}
