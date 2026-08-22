using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Unswarm.Api.Configuration;
using Unswarm.Api.Middleware;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

public sealed class ApiKeyAuthMiddlewareTests
{
    private sealed class NextTracker
    {
        public bool Called { get; set; }
    }

    private static (ApiKeyAuthMiddleware middleware, DefaultHttpContext context, NextTracker tracker, IApiKeyStore store) CreateSut(
        AuthOptions options,
        IApiKeyStore? store = null,
        string path = "/api/agents",
        string? apiKeyHeader = null,
        string? authorizationHeader = null)
    {
        var ctxStore = store ?? TestApiKeyStore.Create();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        // Capturable body: the default DefaultHttpContext body discards writes,
        // so response-body assertions would always read an empty string.
        context.Response.Body = new MemoryStream();

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

        var middleware = new ApiKeyAuthMiddleware(
            next, ctxStore, Options.Create(options), NullLogger<ApiKeyAuthMiddleware>.Instance);
        return (middleware, context, tracker, ctxStore);
    }

    private static bool HasScopeClaim(HttpContext context, ApiKeyScope scope)
    {
        return context.User.Identities
            .OfType<ClaimsIdentity>()
            .Any(i => i.FindFirst(ApiKeyAuthMiddleware.ScopeClaimType)?.Value == scope.ToString());
    }

    [Fact]
    public async Task CookiePrincipal_PassesThrough_EvenWithoutKey()
    {
        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/ws/agent"] });

        context.User = new ClaimsPrincipal(new ClaimsIdentity("Cookie"));

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonProtectedPath_PassesThrough()
    {
        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/v1", "/api/agents", "/ws/agent"] },
            path: "/api/stats");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task ProtectedPath_NoKeys_Anonymous_BlockedWithBootstrapHint()
    {
        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/ws/agent"] }, path: "/ws/agent");

        // Fail-closed: empty key store + anonymous caller must be rejected.
        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task ProtectedPath_NoKeys_Anonymous_ResponseBodyExplainsBootstrap()
    {
        var (middleware, context, _, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/v1"] }, path: "/v1");

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("bootstrap", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API key", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtectedPath_NoKeys_CookiePrincipal_Allowed()
    {
        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/api/agents"] });

        // Dashboard admin with a cookie principal must still get through so
        // the first API key can be created from the UI.
        context.User = new ClaimsPrincipal(new ClaimsIdentity("Cookie"));

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task ProtectedPath_NoKeys_ValidKey_Allowed()
    {
        var store = TestApiKeyStore.Create();
        var created = await store.CreateAsync("first", ApiKeyScope.Inference, "first-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/v1"] }, store,
            path: "/v1", apiKeyHeader: created.Secret);

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(HasScopeClaim(context, ApiKeyScope.Inference));
    }

    [Fact]
    public async Task ProtectedPath_NoKey_KeyOfScope_Exists_Returns401()
    {
        var store = TestApiKeyStore.Create();
        await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/ws/agent"] }, store,
            path: "/ws/agent");

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task ValidAgentKey_WsAgent_PassesAndSetsScopeClaim()
    {
        var store = TestApiKeyStore.Create();
        var created = await store.CreateAsync("machine-b", ApiKeyScope.Agent, "agent-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/ws/agent"] }, store,
            path: "/ws/agent", authorizationHeader: $"Bearer {created.Secret}");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);

        Assert.True(HasScopeClaim(context, ApiKeyScope.Agent));
        var key = await store.GetAsync(created.Id);
        Assert.NotNull(key?.LastUsedAt);
    }

    [Fact]
    public async Task ValidInferenceKey_V1_PassesAndSetsInferenceScope()
    {
        var store = TestApiKeyStore.Create();
        var created = await store.CreateAsync("ci", ApiKeyScope.Inference, "ci-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/v1"] }, store,
            path: "/v1", apiKeyHeader: created.Secret);

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);

        Assert.True(HasScopeClaim(context, ApiKeyScope.Inference));
    }

    [Fact]
    public async Task InvalidKey_Returns401()
    {
        var store = TestApiKeyStore.Create();
        await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/ws/agent"] }, store,
            path: "/ws/agent", apiKeyHeader: "not-the-secret");

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task XApiKeyTakesPrecedence_WhenBothHeadersPresent()
    {
        var store = TestApiKeyStore.Create();
        var created = await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/ws/agent"] }, store,
            path: "/ws/agent",
            apiKeyHeader: created.Secret,
            authorizationHeader: "Bearer wrong");

        await middleware.InvokeAsync(context);

        Assert.True(tracker.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task RevokedKey_CannotAuthenticate()
    {
        var store = TestApiKeyStore.Create();
        var created = await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");
        await store.RevokeAsync(created.Id);

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/ws/agent"] }, store,
            path: "/ws/agent", apiKeyHeader: created.Secret);

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task InferenceKey_AgentPath_Rejected()
    {
        var store = TestApiKeyStore.Create();
        // Seed an Agent key so the agent surface is activated, then present an Inference key.
        await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-activate");
        var created = await store.CreateAsync("ci", ApiKeyScope.Inference, "ci-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/api/agents", "/ws/agent"] }, store,
            path: "/api/agents", apiKeyHeader: created.Secret);

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task AgentKey_InferencePath_Rejected()
    {
        var store = TestApiKeyStore.Create();
        // Seed an Inference key so the inference surface is activated, then present an Agent key.
        await store.CreateAsync("ci", ApiKeyScope.Inference, "ci-activate");
        var created = await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        var (middleware, context, tracker, _) = CreateSut(
            new AuthOptions { ProtectedPaths = ["/v1"] }, store,
            path: "/v1", apiKeyHeader: created.Secret);

        await middleware.InvokeAsync(context);

        Assert.False(tracker.Called);
        Assert.Equal(401, context.Response.StatusCode);
    }
}
