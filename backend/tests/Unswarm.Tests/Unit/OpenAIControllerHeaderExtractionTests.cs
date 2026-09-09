using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Unswarm.Api.Controllers;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for the static ExtractForwardedHeaders helper in OpenAIController.
///
/// ExtractForwardedHeaders copies client HTTP headers into a dictionary
/// suitable for forwarding to upstream inference engines.  It deliberately
/// excludes structural / security headers (Authorization, Content-Type,
/// Content-Length, Host) as well as any X-Forwarded-* hop-by-hop header
/// that ASP.NET Core would not have already consumed.
/// </summary>
public sealed class OpenAIControllerHeaderExtractionTests
{
    // ── Whitelisted headers (should be included) ──────────────────────

    [Fact]
    public void ExtractForwardedHeaders_WithAcceptHeader_IncludesIt()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Single(result);
        Assert.Equal("application/json", result["Accept"]);
    }

    [Fact]
    public void ExtractForwardedHeaders_WithCustomXApiHeader_IncludesIt()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-Correlation-Id"] = "abc-123"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Single(result);
        Assert.Equal("abc-123", result["X-Correlation-Id"]);
    }

    [Fact]
    public void ExtractForwardedHeaders_WithMultipleWhitelistedHeaders_IncludesAll()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "text/plain",
            ["X-Correlation-Id"] = "corr-1",
            ["X-Request-Source"] = "web"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Equal(3, result.Count);
        Assert.Equal("text/plain", result["Accept"]);
        Assert.Equal("corr-1", result["X-Correlation-Id"]);
        Assert.Equal("web", result["X-Request-Source"]);
    }

    [Fact]
    public void ExtractForwardedHeaders_WithNonExcludedPrefixHeader_IncludesIt()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-My-Custom-Header"] = "value"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Single(result);
        Assert.Equal("value", result["X-My-Custom-Header"]);
    }

    // ── Blacklisted headers (should be excluded) ──────────────────────

    [Fact]
    public void ExtractForwardedHeaders_Authorization_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Authorization"] = "Bearer secret-key"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_ContentType_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Content-Type"] = "application/json"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_ContentLength_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Content-Length"] = "1234"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_Host_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Host"] = "api.example.com"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_XForwardedFor_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-Forwarded-For"] = "192.168.1.1"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_XForwardedHost_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-Forwarded-Host"] = "original-host.example.com"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_XForwardedProto_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-Forwarded-Proto"] = "https"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_XForwardedAnyPrefix_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-Forwarded-Method"] = "POST"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    // ── Mixed headers ─────────────────────────────────────────────────

    [Fact]
    public void ExtractForwardedHeaders_MixedHeaders_ExcludesBlacklisted()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json",
            ["Authorization"] = "Bearer secret",
            ["X-Correlation-Id"] = "corr-1",
            ["Content-Type"] = "application/json",
            ["X-Forwarded-For"] = "10.0.0.1",
            ["User-Agent"] = "MyClient/1.0",
            ["Host"] = "example.com"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Equal(3, result.Count);
        Assert.Equal("application/json", result["Accept"]);
        Assert.Equal("corr-1", result["X-Correlation-Id"]);
        Assert.Equal("MyClient/1.0", result["User-Agent"]);
    }

    // ── Case insensitivity ────────────────────────────────────────────

    [Fact]
    public void ExtractForwardedHeaders_AuthorizationMixedCase_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["authorization"] = "Bearer secret"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_XForwardedMixedCase_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["x-forwarded-for"] = "10.0.0.1"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    // ── Empty / null-like values ──────────────────────────────────────

    [Fact]
    public void ExtractForwardedHeaders_EmptyValue_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-Empty-Header"] = ""
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_NullValue_Excluded()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["X-Null-Header"] = null
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractForwardedHeaders_NoHeaders_ReturnsEmptyDictionary()
    {
        var headers = BuildHeaders([]);

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Empty(result);
    }

    // ── Value selection ───────────────────────────────────────────────

    [Fact]
    public void ExtractForwardedHeaders_MultiValueHeader_TakesFirstValue()
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["Accept"] = new Microsoft.Extensions.Primitives.StringValues(new[] { "text/html", "application/json" })
        };
        var headers = new HeaderDictionary(dict);

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        Assert.Single(result);
        // FirstOrDefault() returns the first value from the StringValues array
        Assert.Equal("text/html", result["Accept"]);
    }

    // ── Result dictionary properties ──────────────────────────────────

    [Fact]
    public void ExtractForwardedHeaders_ResultHasOrdinalIgnoreCaseComparer()
    {
        var headers = BuildHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json"
        });

        var result = OpenAIController.ExtractForwardedHeaders(headers);

        // Dictionary should be case-insensitive for lookups
        Assert.Equal("application/json", result["accept"]);
        Assert.Equal("application/json", result["ACCEPT"]);
    }

    // ── Helper ────────────────────────────────────────────────────────

    private static IHeaderDictionary BuildHeaders(Dictionary<string, string?> headers)
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        foreach (var kvp in headers)
        {
            dict[kvp.Key] = kvp.Value ?? string.Empty;
        }
        return new HeaderDictionary(dict);
    }
}
