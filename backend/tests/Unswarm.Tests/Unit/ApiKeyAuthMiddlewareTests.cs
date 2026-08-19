using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Unswarm.Api.Configuration;
using Unswarm.Api.Middleware;

namespace Unswarm.Tests.Unit;

public sealed class ApiKeyAuthMiddlewareTests
{
    private sealed class NextTracker
    {
        public bool Called { get; set; }
    }

    private static (ApiKeyAuthMiddleware middleware, DefaultHttpContext context, NextTracker tracker) CreateSut(
        AuthOptions options,
        string path = "/api/agents",
        string? apiKeyHeader = null,
        string? authorizationHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        if (apiKeyHeader != null)
            context.Request.Headers["X-Api-Key"] = apiKeyHeader;
        if (authorizationHeader != null)
            context.Request.Headers["Authorization"] = authorizationHeader;

        var tracker = new NextTracker();
        RequestDelegate next = _ =>
        {
            tracker.Called = true;
            return Task.CompletedTask;
        };

        var middleware = new ApiKeyAuthMiddleware(next, Options.Create(options), NullLogger<ApiKeyAuthMiddleware>.Instance);
        return (middleware, context, tracker);
    }

    [Fact]
    public async Task ValidKey_ProtectedPath_CallsNext()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/api/agents"] },
            path: "/api/agents",
            apiKeyHeader: "secret-key");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidKey_ProtectedPath_Returns401()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/api/agents"] },
            path: "/api/agents",
            apiKeyHeader: "wrong-key");

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task MissingKey_ProtectedPath_Returns401()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/api/agents"] },
            path: "/api/agents");

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task AuthDisabled_EmptyKey_CallsNext()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "", ProtectedPaths = ["/api/agents"] },
            path: "/api/agents");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonProtectedPath_NoKey_CallsNext()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/api/agents"] },
            path: "/api/containers");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task SubPathOfProtected_IsAlsoProtected()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/api/agents"] },
            path: "/api/agents/abc123/status");

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task ValidBearerToken_ProtectedPath_CallsNext()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/ws/agent"] },
            path: "/ws/agent",
            authorizationHeader: "Bearer secret-key");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task WrongBearerToken_ProtectedPath_Returns401()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/ws/agent"] },
            path: "/ws/agent",
            authorizationHeader: "Bearer wrong");

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task PathPrefixMatch_CaseInsensitive_Returns401()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/api/agents"] },
            path: "/API/AGENTS");

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task WhitespaceKey_TreatedAsDisabled_CallsNext()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "   ", ProtectedPaths = ["/api/agents"] },
            path: "/api/agents");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
    }

    [Fact]
    public async Task XApiKeyTakesPrecedence_WhenBothHeadersPresent()
    {
        var (middleware, context, tracker) = CreateSut(
            new AuthOptions { ApiKey = "secret-key", ProtectedPaths = ["/api/agents"] },
            path: "/api/agents",
            apiKeyHeader: "secret-key",
            authorizationHeader: "Bearer wrong");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
    }
}
