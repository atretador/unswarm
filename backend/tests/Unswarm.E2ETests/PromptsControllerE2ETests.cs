using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unswarm.E2ETests;

/// <summary>
/// E2E tests for the Prompts controller. All persistence hits the real
/// SQLite database — no faked stores.
/// </summary>
public sealed class PromptsControllerE2ETests
{
    private const string Base = "/api/prompts";

    [Fact]
    public async Task ListPrompts_EmptyByDefault()
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
    public async Task CreatePrompt_ReturnsCreated()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync(Base, new
        {
            name = "Test Prompt",
            text = "Hello, tell me about {topic}"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("Test Prompt", json.GetProperty("name").GetString());
        Assert.Equal("Hello, tell me about {topic}", json.GetProperty("text").GetString());
        Assert.False(json.GetProperty("isDefault").GetBoolean());
        Assert.Equal(1, json.GetProperty("currentVersion").GetInt32());

        // Verify it appears in the list
        var list = await client.GetFromJsonAsync<JsonElement[]>(Base, cts.Token);
        Assert.NotNull(list);
        Assert.Single(list);
    }

    [Fact]
    public async Task CreatePrompt_WithMaxTokens()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync(Base, new
        {
            name = "Custom MaxTokens Prompt",
            text = "Some prompt text",
            maxTokens = 512
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(512, json.GetProperty("maxTokens").GetInt32());
    }

    [Fact]
    public async Task UpdatePrompt_ModifiesFields()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create
        var create = await client.PostAsJsonAsync(Base, new
        {
            name = "Original",
            text = "Original text"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        // Update
        var update = await client.PutAsJsonAsync($"{Base}/{id}", new
        {
            name = "Updated",
            text = "Updated text"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("Updated", updated.GetProperty("name").GetString());
        Assert.Equal("Updated text", updated.GetProperty("text").GetString());
    }

    [Fact]
    public async Task DeletePrompt_RemovesFromList()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create
        var create = await client.PostAsJsonAsync(Base, new
        {
            name = "To Delete",
            text = "Delete me"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
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
    public async Task DeletePrompt_Nonexistent_Returns404()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.DeleteAsync($"{Base}/nonexistent-id", cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetDefaultPrompt_SetsIsDefault()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create two prompts
        var r1 = await client.PostAsJsonAsync(Base, new
        {
            name = "Prompt A",
            text = "Text A"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var id1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        var r2 = await client.PostAsJsonAsync(Base, new
        {
            name = "Prompt B",
            text = "Text B"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var id2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        // Set Prompt A as default
        var setDefault = await client.PostAsync($"{Base}/{id1}/default", null, cts.Token);
        Assert.Equal(HttpStatusCode.OK, setDefault.StatusCode);

        var defaultJson = JsonDocument.Parse(await setDefault.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(id1, defaultJson.GetProperty("id").GetString());
        Assert.True(defaultJson.GetProperty("isDefault").GetBoolean());

        // Verify Prompt B is not default
        var bGet = await client.GetFromJsonAsync<JsonElement>($"{Base}/{id2}", cts.Token);
        Assert.False(bGet.GetProperty("isDefault").GetBoolean());

        // Switch default to Prompt B
        var switchDefault = await client.PostAsync($"{Base}/{id2}/default", null, cts.Token);
        Assert.Equal(HttpStatusCode.OK, switchDefault.StatusCode);

        var switchedJson = JsonDocument.Parse(await switchDefault.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal(id2, switchedJson.GetProperty("id").GetString());
        Assert.True(switchedJson.GetProperty("isDefault").GetBoolean());

        // Verify Prompt A is no longer default
        var aGet = await client.GetFromJsonAsync<JsonElement>($"{Base}/{id1}", cts.Token);
        Assert.False(aGet.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task ListPromptVersions_AfterCreate_ShowsV1()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create
        var create = await client.PostAsJsonAsync(Base, new
        {
            name = "Versioned Prompt",
            text = "Initial text"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        // List versions
        var versions = await client.GetFromJsonAsync<JsonElement[]>($"{Base}/{id}/versions", cts.Token);
        Assert.NotNull(versions);
        Assert.Single(versions);

        var v1 = versions[0];
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.Equal("Initial text", v1.GetProperty("text").GetString());
        Assert.Equal(id, v1.GetProperty("promptId").GetString());
    }

    [Fact]
    public async Task RollbackPrompt_RestoresOldVersion()
    {
        await using var factory = new UnswarmApiFactory();
        var client = await factory.CreateControlClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Create with original text
        var create = await client.PostAsJsonAsync(Base, new
        {
            name = "Rollback Prompt",
            text = "Original version text"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cts.Token))
            .RootElement.GetProperty("id").GetString()!;

        // Update with new text (creates v2)
        var update = await client.PutAsJsonAsync($"{Base}/{id}", new
        {
            name = "Rollback Prompt",
            text = "Updated version text"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // Verify current text is the updated one
        var current = await client.GetFromJsonAsync<JsonElement>($"{Base}/{id}", cts.Token);
        Assert.Equal("Updated version text", current.GetProperty("text").GetString());
        Assert.Equal(2, current.GetProperty("currentVersion").GetInt32());

        // Rollback to v1
        var rollback = await client.PostAsJsonAsync($"{Base}/{id}/rollback", new
        {
            version = 1
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);

        var rolledBack = JsonDocument.Parse(await rollback.Content.ReadAsStringAsync(cts.Token)).RootElement;
        Assert.Equal("Original version text", rolledBack.GetProperty("text").GetString());
    }

    [Fact]
    public async Task CreatePrompt_WithoutAuth_Returns401()
    {
        await using var factory = new UnswarmApiFactory();
        var client = factory.CreateClient(); // no auth cookie

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.PostAsJsonAsync(Base, new
        {
            name = "Unauthenticated",
            text = "Should fail"
        }, cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
