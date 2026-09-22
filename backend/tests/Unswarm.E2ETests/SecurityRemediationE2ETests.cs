using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests;

/// <summary>
/// End-to-end coverage for the C3/H1/H2/H3 authorization remediations
/// (workstreams A1–A4). Controller attributes are not evaluated when actions are
/// called directly, so these scenarios exercise the real pipeline: middleware
/// scope checks, named authorization policies, and permission filters.
/// </summary>
public sealed class SecurityRemediationE2ETests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<(string Id, string Secret)> CreateControlPlaneKeyAsync(
        HttpClient admin, Dictionary<string, string> permissions)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var response = await admin.PostAsJsonAsync("/api/api-keys/control-plane", new
        {
            name = "e2e-cp-" + Guid.NewGuid().ToString("N")[..8],
            permissions
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        return (json.GetProperty("id").GetString()!, json.GetProperty("secret").GetString()!);
    }

    private static async Task<(string Id, string Secret)> CreateAgentKeyAsync(HttpClient admin)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var response = await admin.PostAsJsonAsync("/api/api-keys/agent", new
        {
            name = "e2e-agent-" + Guid.NewGuid().ToString("N")[..8]
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        return (json.GetProperty("id").GetString()!, json.GetProperty("secret").GetString()!);
    }

    private static HttpClient KeyClient(UnswarmApiFactory factory, string secret)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return client;
    }

    private static async Task<HttpClient> CreateUserClientAsync(UnswarmApiFactory factory, HttpClient admin)
    {
        var username = "e2e-user-" + Guid.NewGuid().ToString("N")[..8];
        const string password = "SecurePassword123!";
        using var cts = new CancellationTokenSource(Timeout);
        var create = await admin.PostAsJsonAsync("/api/users", new { username, password }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = login.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith(".Unswarm.Auth=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        return client;
    }

    // ── A1/C3: /api/scripts is ControlPlaneAccess only ─────────────────

    [Fact]
    public async Task Scripts_UserCookie_IsForbidden()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var user = await CreateUserClientAsync(factory, admin);

        var response = await user.GetAsync("/api/scripts");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Scripts_AgentKey_IsDenied()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateAgentKeyAsync(admin);
        var agentClient = KeyClient(factory, secret);

        var response = await agentClient.GetAsync("/api/scripts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Scripts_InferenceKey_IsDenied()
    {
        await using var factory = new UnswarmApiFactory();
        var inference = factory.CreateInferenceClient();

        var response = await inference.GetAsync("/api/scripts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Scripts_AdminCookie_IsAllowed()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();

        var response = await admin.GetAsync("/api/scripts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── A2/H2: /api/users is AdminOnly ─────────────────────────────────

    [Fact]
    public async Task Users_UsersRwKey_IsForbiddenOnListAndReset()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["users"] = "rw" });
        var keyClient = KeyClient(factory, secret);

        using var cts = new CancellationTokenSource(Timeout);
        var list = await keyClient.GetAsync("/api/users", cts.Token);
        // AdminOnly denies the API-key principal. Without a cookie the challenge
        // surfaces as 401; an authenticated-but-unauthorized cookie would be 403.
        Assert.Contains(list.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        // Find the admin id with the admin client, then attempt a reset.
        var adminList = await admin.GetAsync("/api/users", cts.Token);
        Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);
        var users = JsonDocument.Parse(await adminList.Content.ReadAsStringAsync(cts.Token)).RootElement;
        var adminId = users.EnumerateArray()
            .First(u => u.GetProperty("username").GetString() == UnswarmApiFactory.AdminUsername)
            .GetProperty("id").GetString();

        var reset = await keyClient.PostAsJsonAsync($"/api/users/{adminId}/reset-password",
            new { newPassword = "ShouldNotHappen123!" }, cts.Token);
        Assert.Contains(reset.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    // ── A3/H1: apikeys grants ──────────────────────────────────────────

    [Fact]
    public async Task ApiKeys_ApiKeysRwKey_MayGrantSubsetControlPlane_ButNotSuperset()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["apikeys"] = "rw" });
        var keyClient = KeyClient(factory, secret);

        using var cts = new CancellationTokenSource(Timeout);

        // Subset of the caller's own apikeys:rw grant is allowed.
        var subset = await keyClient.PostAsJsonAsync("/api/api-keys/control-plane", new
        {
            name = "subset-key",
            permissions = new Dictionary<string, string> { ["apikeys"] = "r" }
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, subset.StatusCode);

        // A permission the caller does not hold is denied.
        var superset = await keyClient.PostAsJsonAsync("/api/api-keys/control-plane", new
        {
            name = "superset-key",
            permissions = new Dictionary<string, string> { ["users"] = "rw" }
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, superset.StatusCode);
    }

    [Fact]
    public async Task ApiKeys_ApiKeysRwKey_CannotCreateInferenceOrAgentKeys()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["apikeys"] = "rw" });
        var keyClient = KeyClient(factory, secret);

        using var cts = new CancellationTokenSource(Timeout);

        var inference = await keyClient.PostAsJsonAsync("/api/api-keys", new { name = "inf" }, cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, inference.StatusCode);

        var agent = await keyClient.PostAsJsonAsync("/api/api-keys/agent", new { name = "agent" }, cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
    }

    [Fact]
    public async Task ApiKeys_ApiKeysRwKey_CannotRotateAgentKey()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["apikeys"] = "rw" });
        var (agentId, _) = await CreateAgentKeyAsync(admin);
        var keyClient = KeyClient(factory, secret);

        using var cts = new CancellationTokenSource(Timeout);
        var rotate = await keyClient.PostAsync($"/api/api-keys/{agentId}/rotate", null, cts.Token);

        Assert.Equal(HttpStatusCode.Forbidden, rotate.StatusCode);
    }

    // ── A4/H3: runtimes:rw cannot create/register/retarget host scripts ─

    [Fact]
    public async Task Containers_RuntimesRwKey_DeniedOnCreate()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["runtimes"] = "rw" });
        var keyClient = KeyClient(factory, secret);

        using var cts = new CancellationTokenSource(Timeout);
        var response = await keyClient.PostAsJsonAsync("/api/containers/create", new
        {
            image = "docker.io/library/alpine:latest",
            name = "evil"
        }, cts.Token);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Theory]
    [InlineData("host")]
    [InlineData(" host")]
    [InlineData("host ")]
    public async Task Containers_RuntimesRwKey_DeniedOnHostScriptRegister(string agent)
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["runtimes"] = "rw" });
        var keyClient = KeyClient(factory, secret);

        using var cts = new CancellationTokenSource(Timeout);
        var response = await keyClient.PostAsJsonAsync("/api/containers/register", new
        {
            displayName = "evil-script",
            image = "evil:latest",
            runtimeKind = "script",
            launcherPath = "/tmp/evil.sh",
            agent,
            containerPort = 8080
        }, cts.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(" host")]
    [InlineData("host ")]
    public async Task Containers_RuntimesRwKey_DeniedOnWhitespaceHostScriptStart(string agent)
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["runtimes"] = "rw" });
        var keyClient = KeyClient(factory, secret);

        await factory.Registry.CreateAsync(new RegisteredRuntime
        {
            Id = "e2e-whitespace-host-script",
            DisplayName = "e2e-whitespace-host-script",
            Image = "e2e-script:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = "/tmp/e2e-script.sh",
            Agent = agent,
            ContainerPort = 8080,
            Status = ContainerRegistrationStatus.Registered,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        using var cts = new CancellationTokenSource(Timeout);
        var start = await keyClient.PostAsync(
            "/api/containers/registered/e2e-whitespace-host-script/start", null, cts.Token);

        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
    }

    [Fact]
    public async Task Containers_RuntimesRwKey_DeniedOnHostScriptStartAndPortRetarget()
    {
        await using var factory = new UnswarmApiFactory();
        var admin = await factory.CreateControlClientAsync();
        var (_, secret) = await CreateControlPlaneKeyAsync(admin, new() { ["runtimes"] = "rw" });
        var keyClient = KeyClient(factory, secret);

        await factory.Registry.CreateAsync(new RegisteredRuntime
        {
            Id = "e2e-host-script",
            DisplayName = "e2e-host-script",
            Image = "e2e-script:latest",
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = "/tmp/e2e-script.sh",
            Agent = "host",
            ContainerPort = 8080,
            Status = ContainerRegistrationStatus.Registered,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        using var cts = new CancellationTokenSource(Timeout);

        var start = await keyClient.PostAsync(
            "/api/containers/registered/e2e-host-script/start", null, cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);

        var retarget = await keyClient.PutAsJsonAsync(
            "/api/containers/registered/e2e-host-script",
            new { mappedPort = 9999 }, cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, retarget.StatusCode);
    }
}
