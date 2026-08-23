using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services.Scheduler;
using Unswarm.E2ETests.Fakes;

namespace Unswarm.E2ETests;

/// <summary>
/// Boots the real Unswarm API host in-memory with all external dependencies
/// (Docker, health checks, inference upstream, registry, settings, API keys)
/// replaced by in-memory fakes, and the database pointed at a private in-memory
/// SQLite instance so tests never touch ~/.config/unswarm.
/// </summary>
public sealed class UnswarmApiFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "e2e-admin-password";

    /// <summary>Seeds the admin password before any host boots (read by Program.cs).</summary>
    [ModuleInitializer]
    internal static void Bootstrap()
        => Environment.SetEnvironmentVariable("UNSWARM_ADMIN_PASSWORD", AdminPassword);

    public FakeContainerRegistry Registry { get; } = new();
    public FakeInferenceProxy Inference { get; } = new();
    public FakeSettingsStore SettingsStore { get; } = new();
    public FakeLogStore Logs { get; } = new();
    public FakeStatsTracker Stats { get; } = new();
    public FakeHealthChecker Health { get; } = new();
    public FakeApiKeyStore ApiKeys { get; } = new();

    public FakeDockerController HostDocker { get; } = new() { IdPrefix = "host" };
    public FakeDockerControllerRouter Router { get; }

    private readonly Settings? _seededSettings;

    /// <param name="seededSettings">
    /// Optional Settings seeded into the settings store before the host boots
    /// (e.g. EnableParallelSlotSkip scenarios). Also used to build the DI
    /// SchedulerSettings snapshot so snapshot fields (skipsRemaining) reflect it.
    /// </param>
    public UnswarmApiFactory(Settings? seededSettings = null)
    {
        _seededSettings = seededSettings;
        Router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = HostDocker
        });

        if (seededSettings is not null)
            SettingsStore.UpdateAsync(seededSettings).GetAwaiter().GetResult();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            ReplaceDatabase(services);
            ReplaceFakes(services);

            // Align the injected SchedulerSettings snapshot with the seeded store
            // (the queue snapshot endpoint reads skip budget from this singleton).
            if (_seededSettings is not null)
            {
                services.RemoveAll<SchedulerSettings>();
                services.AddSingleton(SchedulerSettings.FromSettings(_seededSettings));
            }

            // Disable the global rate limiter: snapshot polling during flight would
            // otherwise trip the 60 req/min management window with 429s.
            services.PostConfigure<RateLimiterOptions>(options =>
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter<string>("e2e")));
        });
    }

    /// <summary>
    /// Re-points every DbContext creation at a single open in-memory SQLite
    /// connection (unique per factory instance). Migrations run against it at
    /// startup exactly as in production.
    /// </summary>
    private void ReplaceDatabase(IServiceCollection services)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open(); // shared connection keeps the in-memory DB alive

        services.RemoveAll<DbContextOptions<UnswarmDbContext>>();
        services.AddScoped<DbContextOptions<UnswarmDbContext>>(_ =>
            new DbContextOptionsBuilder<UnswarmDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(SqliteTuningInterceptor.Instance)
                .Options);

        services.RemoveAll<Func<UnswarmDbContext>>();
        services.AddSingleton<Func<UnswarmDbContext>>(_ => () =>
            new UnswarmDbContext(new DbContextOptionsBuilder<UnswarmDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(SqliteTuningInterceptor.Instance)
                .Options));
    }

    private void ReplaceFakes(IServiceCollection services)
    {
        ReplaceSingleton<IContainerRegistry>(services, Registry);
        ReplaceSingleton<ISettingsStore>(services, SettingsStore);
        ReplaceSingleton<ILogStore>(services, Logs);
        ReplaceSingleton<IStatsTracker>(services, Stats);
        ReplaceSingleton<IHealthChecker>(services, Health);
        ReplaceSingleton<IInferenceProxy>(services, Inference);
        ReplaceSingleton<IApiKeyStore>(services, ApiKeys);
        ReplaceSingleton<IDockerController>(services, HostDocker);
        ReplaceSingleton<IDockerControllerRouter>(services, Router);
        ReplaceSingleton<IModelTargetResolver>(services, new FakeModelTargetResolver());
    }

    private static void ReplaceSingleton<T>(IServiceCollection services, T instance) where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton<T>(instance);
    }
}
