using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// E2E tests for the Router Profile controller. All persistence hits the real
/// SQLite database — no faked stores.
/// </summary>
public sealed class RouterProfileControllerE2ETests
{
    private const string Base = "/api/router-profiles";

    [Fact]
    public async Task ListRouterProfiles_EmptyByDefault()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync(Base, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task CreateRouterProfile_ReturnsCreated()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync(Base, new
        {
            name = "Test Profile",
            mode = "auto",
            entries = new[]
            {
                new { modelId = "cloud/openai/gpt-4o", priority = 0, isEnabled = true }
            }
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("Test Profile", json.GetProperty("name").GetString());
        Assert.NotEmpty(json.GetProperty("id").GetString()!);
        Assert.Equal("auto", json.GetProperty("mode").GetString());
        Assert.Equal(1, json.GetProperty("entries").GetArrayLength());

        // Verify it appears in the list
        var list = await client.GetFromJsonAsync<JsonElement[]>(Base, cts.Token);
        Assert.NotNull(list);
        Assert.Single(list);
    }

    [Fact]
    public async Task CreateRouterProfile_MultipleProfiles()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var r1 = await client.PostAsJsonAsync(Base, new
        {
            name = "Profile A",
            mode = "auto"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var r2 = await client.PostAsJsonAsync(Base, new
        {
            name = "Profile B",
            mode = "manual"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement[]>(Base, cts.Token);
        Assert.NotNull(list);
        Assert.Equal(2, list.Length);
    }

    [Fact]
    public async Task UpdateRouterProfile_ModifiesFields()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create
        var create = await client.PostAsJsonAsync(Base, new
        {
            name = "Original Name",
            mode = "auto",
            entries = new[]
            {
                new { modelId = "model-a", priority = 0, isEnabled = true }
            }
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        // Update
        var update = await client.PutAsJsonAsync($"{Base}/{id}", new
        {
            name = "Updated Name",
            mode = "manual",
            entries = new[]
            {
                new { modelId = "model-b", priority = 0, isEnabled = true }
            }
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("Updated Name", updated.GetProperty("name").GetString());
        Assert.Equal("manual", updated.GetProperty("mode").GetString());
        Assert.Equal("model-b", updated.GetProperty("entries")[0].GetProperty("modelId").GetString());
    }

    [Fact]
    public async Task DeleteRouterProfile_RemovesFromList()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create
        var create = await client.PostAsJsonAsync(Base, new
        {
            name = "To Delete",
            mode = "auto"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        // Delete
        var del = await client.DeleteAsync($"{Base}/{id}", cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Verify gone
        var list = await client.GetFromJsonAsync<JsonElement[]>(Base, cts.Token);
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task DeleteRouterProfile_Nonexistent_Returns404()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.DeleteAsync($"{Base}/nonexistent-id", cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetActiveEntry_ChangesActiveModel()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create profile with two entries
        var create = await client.PostAsJsonAsync(Base, new
        {
            name = "Active Test",
            mode = "auto",
            entries = new object[]
            {
                new { modelId = "model-primary", priority = 0, isEnabled = true },
                new { modelId = "model-fallback", priority = 1, isEnabled = true }
            }
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        // Set active entry
        var patch = await client.PatchAsJsonAsync($"{Base}/{id}/active-entry", new
        {
            activeModelId = "model-fallback"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var profile = JsonDocument.Parse(await patch.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("model-fallback", profile.GetProperty("activeModelId").GetString());
    }

    [Fact]
    public async Task GetRouterProfileStatus_ReturnsStatusMap()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync($"{Base}/status", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        // No active requests initially — status map is empty
        Assert.Empty(json.EnumerateObject());
    }

    [Fact]
    public async Task CreateRouterProfile_WithoutAuth_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient(); // no auth cookie

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync(Base, new
        {
            name = "Unauthenticated",
            mode = "auto"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
