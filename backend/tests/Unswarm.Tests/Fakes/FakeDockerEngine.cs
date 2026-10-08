using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// State behind <see cref="FakeDockerEngine"/>. Tests configure canned JSON
/// responses; the engine records request lines and effect calls for assertions.
/// </summary>
public sealed class FakeDockerEngineState
{
    public List<string> Requests { get; } = [];

    /// <summary>Body for GET .../containers/json.</summary>
    public string ContainersListJson { get; set; } = "[]";

    /// <summary>Inspect JSON keyed by container id (exact path segment match).</summary>
    public Dictionary<string, string> InspectJsonById { get; } = new(StringComparer.Ordinal);

    /// <summary>Image-inspect JSON keyed by image name; missing → 404 (force pull).</summary>
    public Dictionary<string, string> InspectJsonByImage { get; } = new(StringComparer.Ordinal);

    public string CreateResponseJson { get; set; } = """{"Id":"new-container-id","Warnings":[]}""";
    public int CreateStatusCode { get; set; } = 201;

    public string? LastCreateBody { get; set; }

    public string ImageCreateBody { get; set; } = "";

    public string LogsBody { get; set; } = "";
    public int LogsStatusCode { get; set; } = 200;

    public int StartStatusCode { get; set; } = 204;
    public int StopStatusCode { get; set; } = 204;
    public int RemoveStatusCode { get; set; } = 204;

    public List<string> Started { get; } = [];
    public List<string> Stopped { get; } = [];
    public List<string> Removed { get; } = [];
    public int ImageCreateCalls { get; private set; }

    public bool PathRequested(string fragment)
        => Requests.Any(r => r.Contains(fragment, StringComparison.Ordinal));

    public void RecordImageCreate() => ImageCreateCalls++;
}

/// <summary>
/// Hermetic in-process fake Docker Engine. Real HTTP over loopback (Kestrel on
/// 127.0.0.1:0) so the genuine Docker.DotNet HTTP client can talk to it — no
/// Docker socket, no daemon. Matches on path suffix (ignoring the /v1.xx prefix).
/// </summary>
public sealed class FakeDockerEngine : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly FakeDockerEngineState _state;

    private FakeDockerEngine(WebApplication app, Uri baseUri, FakeDockerEngineState state)
    {
        _app = app;
        _state = state;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static async Task<FakeDockerEngine> StartAsync(FakeDockerEngineState? state = null)
    {
        state ??= new FakeDockerEngineState();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        app.Run(ctx => HandleAsync(ctx, state));

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeDockerEngine(app, new Uri(address), state);
    }

    private static async Task HandleAsync(HttpContext ctx, FakeDockerEngineState state)
    {
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value ?? "";
        state.Requests.Add($"{method} {path}");

        // Strip a possible /v1.xx prefix; match on the meaningful suffix.
        if (path.EndsWith("/_ping", StringComparison.Ordinal))
        {
            await WriteTextAsync(ctx, 200, "OK", "text/plain");
            return;
        }

        if (path.EndsWith("/version", StringComparison.Ordinal))
        {
            await WriteJsonAsync(ctx, 200, """{"ApiVersion":"1.41","MinAPIVersion":"1.12","Version":"24.0.0"}""");
            return;
        }

        if (path.EndsWith("/containers/json", StringComparison.Ordinal))
        {
            await WriteJsonAsync(ctx, 200, state.ContainersListJson);
            return;
        }

        if (path.EndsWith("/containers/create", StringComparison.Ordinal))
        {
            state.LastCreateBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            await WriteJsonAsync(ctx, state.CreateStatusCode, state.CreateResponseJson);
            return;
        }

        if (path.EndsWith("/images/create", StringComparison.Ordinal))
        {
            state.RecordImageCreate();
            await WriteTextAsync(ctx, 200, state.ImageCreateBody, "application/json");
            return;
        }

        if (TrySegmentAfter(path, "images", out var imageName) && path.EndsWith("/json", StringComparison.Ordinal))
        {
            if (state.InspectJsonByImage.TryGetValue(imageName, out var imageJson))
                await WriteJsonAsync(ctx, 200, imageJson);
            else
                await WriteJsonAsync(ctx, 404, """{"message":"No such image: " + imageName + ""}""");
            return;
        }

        if (TrySegmentAfter(path, "containers", out var containerId))
        {
            if (path.EndsWith("/logs", StringComparison.Ordinal))
            {
                await WriteTextAsync(ctx, state.LogsStatusCode, state.LogsBody, "text/plain");
                return;
            }

            if (path.EndsWith("/json", StringComparison.Ordinal))
            {
                if (state.InspectJsonById.TryGetValue(containerId, out var inspectJson))
                    await WriteJsonAsync(ctx, 200, inspectJson);
                else
                    await WriteJsonAsync(ctx, 404, """{"message":"No such container: " + containerId + ""}""");
                return;
            }

            if (path.EndsWith("/start", StringComparison.Ordinal))
            {
                state.Started.Add(containerId);
                ctx.Response.StatusCode = state.StartStatusCode;
                if (state.StartStatusCode >= 400)
                    await WriteJsonErrorAsync(ctx, state.StartStatusCode, $"start failed: {containerId}");
                return;
            }

            if (path.EndsWith("/stop", StringComparison.Ordinal))
            {
                state.Stopped.Add(containerId);
                ctx.Response.StatusCode = state.StopStatusCode;
                if (state.StopStatusCode >= 400)
                    await WriteJsonErrorAsync(ctx, state.StopStatusCode, $"stop failed: {containerId}");
                return;
            }

            if (method == HttpMethods.Delete)
            {
                state.Removed.Add(containerId);
                ctx.Response.StatusCode = state.RemoveStatusCode;
                if (state.RemoveStatusCode >= 400)
                    await WriteJsonErrorAsync(ctx, state.RemoveStatusCode, $"remove failed: {containerId}");
                return;
            }
        }

        // Unknown/unhandled — succeed with an empty 200 so version negotiation or
        // incidental probes never fail a test with a connection error.
        ctx.Response.StatusCode = 200;
    }

    /// <summary>
    /// Returns the path segment immediately after <paramref name="marker"/>
    /// (URL-decoded). For images, joins remaining segments before the trailing
    /// /json to support "repo/name:tag".
    /// </summary>
    private static bool TrySegmentAfter(string path, string marker, out string segment)
    {
        segment = "";
        var needle = "/" + marker + "/";
        var idx = path.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0)
            return false;

        var rest = path[(idx + needle.Length)..];
        // Drop a trailing "/json", "/start", "/stop", "/logs" action segment.
        foreach (var action in new[] { "/json", "/start", "/stop", "/logs" })
        {
            if (rest.EndsWith(action, StringComparison.Ordinal))
            {
                rest = rest[..^action.Length];
                break;
            }
        }

        segment = Uri.UnescapeDataString(rest);
        return segment.Length > 0;
    }

    private static async Task WriteJsonAsync(HttpContext ctx, int status, string json)
        => await WriteTextAsync(ctx, status, json, "application/json");

    private static async Task WriteJsonErrorAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        // Content already partially set by status; write a Docker-style error body.
        var bytes = Encoding.UTF8.GetBytes($"{{\"message\":\"{message}\"}}");
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    private static async Task WriteTextAsync(HttpContext ctx, int status, string body, string contentType)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
