using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

public sealed class UsersControllerE2ETests
{
    [Fact]
    public async Task ListUsers_ReturnsAdminUser()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/api/users", cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        var adminUser = users.EnumerateArray().FirstOrDefault(
            u => string.Equals(u.GetProperty("username").GetString(), UnswarmApiFactory.AdminUsername, StringComparison.Ordinal));
        Assert.False(adminUser.ValueKind == JsonValueKind.Undefined, "Admin user must appear in the list");
        Assert.True(adminUser.GetProperty("id").GetString()!.Length > 0);
    }

    [Fact]
    public async Task CreateUser_ValidCredentials_ReturnsOk()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "new-e2e-user",
            password = "SecurePassword123!"
        }, cts.Token);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var body = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("new-e2e-user", body.GetProperty("username").GetString());
        var newUserId = body.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(newUserId));

        // Verify the user appears in the list
        var listResponse = await client.GetAsync("/api/users", cts.Token);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var users = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        var found = users.EnumerateArray().Any(
            u => string.Equals(u.GetProperty("username").GetString(), "new-e2e-user", StringComparison.Ordinal));
        Assert.True(found, "Newly created user must appear in user list");
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_Returns400()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var payload = new { username = "duplicate-user", password = "SecurePassword123!" };
        var first = await client.PostAsJsonAsync("/api/users", payload, cts.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/users", payload, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithoutAdmin_Returns403()
    {
        await using var factory = new UnswarmApiFactory();
        var adminClient = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Step 1: Create a non-admin user (has "User" role only)
        var createResponse = await adminClient.PostAsJsonAsync("/api/users", new
        {
            username = "regular-user",
            password = "SecurePassword123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        // Step 2: Log in as the non-admin user
        var userClient = factory.CreateClient();
        var loginResponse = await userClient.PostAsJsonAsync("/api/auth/login", new
        {
            username = "regular-user",
            password = "SecurePassword123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var setCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .FirstOrDefault(v => v.StartsWith(".Unswarm.Auth=", StringComparison.Ordinal));
        Assert.NotNull(setCookie);
        userClient.DefaultRequestHeaders.Add("Cookie", setCookie!.Split(';')[0]);

        // Step 3: Attempt admin-only action → 403 Forbidden
        var forbiddenResponse = await userClient.PostAsJsonAsync("/api/users", new
        {
            username = "should-not-exist",
            password = "DoesNotMatter123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_OtherUser_ReturnsOk()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create a target user
        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "reset-target",
            password = "OldPassword123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var targetId = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString();

        // Reset their password
        var resetResponse = await client.PostAsJsonAsync($"/api/users/{targetId}/reset-password", new
        {
            newPassword = "BrandNewPassword456!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

        // Verify the target user can login with the new password
        var userClient = factory.CreateClient();
        var loginResponse = await userClient.PostAsJsonAsync("/api/auth/login", new
        {
            username = "reset-target",
            password = "BrandNewPassword456!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_SelfReset_Returns400()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Get admin's ID from the user list
        var listResponse = await client.GetAsync("/api/users", cts.Token);
        var users = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        var adminEntry = users.EnumerateArray().First(
            u => string.Equals(u.GetProperty("username").GetString(), UnswarmApiFactory.AdminUsername, StringComparison.Ordinal));
        var adminId = adminEntry.GetProperty("id").GetString();

        // Attempt to reset own password → 400
        var response = await client.PostAsJsonAsync($"/api/users/{adminId}/reset-password", new
        {
            newPassword = "ShouldNotWork123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("users.cannotResetOwnPassword", json.GetProperty("errorKey").GetString());
    }

    [Fact]
    public async Task DeleteUser_OtherUser_ReturnsOk()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create a target user
        var createResponse = await client.PostAsJsonAsync("/api/users", new
        {
            username = "delete-target",
            password = "DeleteMe123!"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var targetId = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString();

        // Delete them
        var deleteResponse = await client.DeleteAsync($"/api/users/{targetId}", cts.Token);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // Verify they are gone from the list
        var listResponse = await client.GetAsync("/api/users", cts.Token);
        var users = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        var found = users.EnumerateArray().Any(
            u => string.Equals(u.GetProperty("username").GetString(), "delete-target", StringComparison.Ordinal));
        Assert.False(found, "Deleted user must no longer appear in user list");
    }

    [Fact]
    public async Task DeleteUser_SelfDelete_Returns400()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Get admin's ID from the user list
        var listResponse = await client.GetAsync("/api/users", cts.Token);
        var users = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cts.Token)).RootElement;
        var adminEntry = users.EnumerateArray().First(
            u => string.Equals(u.GetProperty("username").GetString(), UnswarmApiFactory.AdminUsername, StringComparison.Ordinal));
        var adminId = adminEntry.GetProperty("id").GetString();

        // Attempt to delete self → 400
        var response = await client.DeleteAsync($"/api/users/{adminId}", cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("users.cannotDeleteOwnAccount", json.GetProperty("errorKey").GetString());
    }
}
