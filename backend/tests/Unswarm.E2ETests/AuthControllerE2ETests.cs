using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

public sealed class AuthControllerE2ETests
{
    [Fact]
    public async Task Login_ValidCredentials_ReturnsOkWithUsername()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = UnswarmApiFactory.AdminUsername,
            password = UnswarmApiFactory.AdminPassword
        }, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(UnswarmApiFactory.AdminUsername, json.GetProperty("username").GetString());
        Assert.True(json.GetProperty("isTempPassword").GetBoolean());
        Assert.True(response.Headers.Contains("Set-Cookie"));
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(".Unswarm.Auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_InvalidPassword_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = UnswarmApiFactory.AdminUsername,
            password = "wrong-password"
        }, cts.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_NonexistentUser_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "nobody",
            password = "irrelevant"
        }, cts.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_Authenticated_ReturnsOk()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsync("/api/auth/logout", null, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Me_Authenticated_ReturnsUsername()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/auth/me", cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(UnswarmApiFactory.AdminUsername, json.GetProperty("username").GetString());
        Assert.True(json.TryGetProperty("isTempPassword", out _));
    }

    [Fact]
    public async Task Me_Unauthenticated_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/auth/me", cts.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ValidCurrentPassword_ReturnsOk()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = UnswarmApiFactory.AdminPassword,
            newPassword = "new-secure-password-123"
        }, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify login works with the new password
        var loginClient = factory.CreateClient();
        var loginResponse = await loginClient.PostAsJsonAsync("/api/auth/login", new
        {
            username = UnswarmApiFactory.AdminUsername,
            password = "new-secure-password-123"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns400()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "definitely-wrong",
            newPassword = "new-secure-password-123"
        }, cts.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("auth.invalidCurrentPassword", json.GetProperty("errorKey").GetString());
    }
}
