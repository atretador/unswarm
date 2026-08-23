using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Unswarm.Core.Models;
using Unswarm.E2ETests.Fakes;

namespace Unswarm.E2ETests;

/// <summary>
/// Shared HTTP + snapshot helpers for E2E scenarios. All polling goes through
/// <see cref="Eventually.UntilAsync"/>; every HTTP call is bounded by a timeout.
///
/// IMPORTANT: use TWO separate clients per test —
///  • <see cref="CreateInferenceClient"/> sends only the X-Api-Key (for /v1);
///    presenting an already-authenticated admin cookie would make
///    ApiKeyAuthMiddleware skip claim-stamping and the InferenceKey policy would
///    reject the request with 403.
///  • <see cref="CreateControlClient"/> carries the admin cookie (for /api/*).
/// </summary>
public static class TestClient
{
    public const string InferenceKey = FakeApiKeyStore.InferenceKeySecret;

    /// <summary>Key-only client for the /v1 inference surface.</summary>
    public static HttpClient CreateInferenceClient(this UnswarmApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", InferenceKey);
        return client;
    }

    /// <summary>Cookie-authenticated client for the /api control plane.</summary>
    public static async Task<HttpClient> CreateControlClientAsync(this UnswarmApiFactory factory)
    {
        var client = factory.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = UnswarmApiFactory.AdminUsername,
            password = UnswarmApiFactory.AdminPassword
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(".Unswarm.Auth=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        return client;
    }

    public static Task<HttpResponseMessage> PostChatCompletionAsync(this HttpClient client, string model)
        => client.PostAsync(
            "/v1/chat/completions",
            new StringContent(
                "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                Encoding.UTF8,
                "application/json"));

    public static async Task<JsonElement> GetSnapshotAsync(this HttpClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.GetAsync("/api/queue/snapshot", cts.Token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement.Clone();
    }

    public static IEnumerable<JsonElement> Items(JsonElement snapshot, string field)
        => snapshot.GetProperty(field).EnumerateArray();

    public static bool AnyItem(JsonElement snapshot, string field, Func<JsonElement, bool> predicate)
        => Items(snapshot, field).Any(predicate);

    public static bool StatusIs(JsonElement item, string status)
        => string.Equals(item.GetProperty("status").GetString(), status, StringComparison.OrdinalIgnoreCase);

    public static bool IsCompleted(JsonElement item) => StatusIs(item, "completed");

    public static string ModelOf(JsonElement item) => item.GetProperty("modelRequested").GetString() ?? "";

    public static string? RuntimeIdOf(JsonElement item) => item.GetProperty("runtimeId").GetString();

    public static IReadOnlyList<string> BlockedBy(JsonElement item)
        => item.GetProperty("blockedByRuntimeIds").EnumerateArray()
            .Select(v => v.GetString() ?? "").ToList();

    public static bool SnapshotShowsCompleted(JsonElement snapshot, string model)
        => AnyItem(snapshot, "recentCompleted", i => ModelOf(i) == model && IsCompleted(i));

    public static bool SnapshotShowsFailed(JsonElement snapshot, string model)
        => AnyItem(snapshot, "recentCompleted", i => ModelOf(i) == model && StatusIs(i, "failed"));

    /// <summary>Registers a container runtime on the host target and maps a model to it.</summary>
    public static async Task<RegisteredRuntime> RegisterRuntimeAsync(
        this UnswarmApiFactory factory,
        string runtimeId,
        string model,
        IReadOnlyList<string>? canRunAlongWith = null,
        int maxConcurrentInferences = 1)
    {
        var runtime = new RegisteredRuntime
        {
            Id = runtimeId,
            Image = $"{runtimeId}-image",
            DisplayName = $"{runtimeId}-image",
            Agent = "host",
            CanRunAlongWith = canRunAlongWith ?? [],
            MaxConcurrentInferences = maxConcurrentInferences
        };
        await factory.Registry.CreateAsync(runtime);
        await factory.Registry.AddModelMappingAsync(runtime.Id, model);
        return runtime;
    }
}
