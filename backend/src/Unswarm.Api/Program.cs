using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Unswarm.Api.Configuration;
using Unswarm.Api.Middleware;
using Unswarm.Api.BackgroundServices;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Core.Services.Validation;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".config", "unswarm", "unswarm.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";

builder.Services.AddDbContext<UnswarmDbContext>(options =>
    options.UseSqlite(connectionString));

// Factory for services that manage their own DbContext lifetime (singleton services)
builder.Services.AddSingleton<Func<UnswarmDbContext>>(sp =>
{
    return () =>
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnswarmDbContext>();
        optionsBuilder.UseSqlite(connectionString);
        return new UnswarmDbContext(optionsBuilder.Options);
    };
});

// ── Auth configuration ─────────────────────────────────────────────────────
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// Env override: if UNSWARM_API_KEY is set and non-empty, use it as the ApiKey
var envApiKey = Environment.GetEnvironmentVariable("UNSWARM_API_KEY");
if (!string.IsNullOrWhiteSpace(envApiKey))
{
    builder.Services.PostConfigure<AuthOptions>(o => o.ApiKey = envApiKey);
}

// ── Core services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ILogStore, LogStore>();
builder.Services.AddSingleton<IStatsTracker, StatsTracker>();
builder.Services.AddSingleton<IDockerController, DockerController>();
builder.Services.AddSingleton<IHealthChecker, HealthChecker>();
builder.Services.AddSingleton<IInferenceProxy, InferenceProxy>();
builder.Services.AddSingleton<ModelDiscoveryService>();
builder.Services.AddScoped<IModelRegistry, ModelRegistry>();
builder.Services.AddScoped<ISettingsStore, SettingsStore>();
builder.Services.AddScoped<ModelValidator>();
builder.Services.AddSingleton<IContainerRegistry, ContainerRegistry>();
builder.Services.AddScoped<IContainerRegistrationService, ContainerRegistrationService>();
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();
builder.Services.AddSingleton<IDockerControllerRouter, DockerControllerRouter>();
builder.Services.AddSingleton<IModelTargetResolver, ModelTargetResolver>();

// ── Scheduler ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton(Channel.CreateBounded<InferenceRequest>(
    new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait }));
builder.Services.AddSingleton<SchedulerSettings>();
builder.Services.AddSingleton<SchedulerWorker>();
builder.Services.AddSingleton<ISchedulerQueue, SchedulerQueue>();

// ── Controllers ───────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// ── Background services ──────────────────────────────────────────────────
builder.Services.AddHostedService<SchedulerHostedService>();
builder.Services.AddHostedService<HealthCheckService>();
builder.Services.AddHostedService<IdleShutdownService>();
builder.Services.AddHostedService<LogRetentionService>();

// ── CORS ──────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// ── Initialize database ──────────────────────────────────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UnswarmDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ── Middleware ────────────────────────────────────────────────────────────
app.UseCors();
app.UseWebSockets();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.MapControllers();

app.Run();
