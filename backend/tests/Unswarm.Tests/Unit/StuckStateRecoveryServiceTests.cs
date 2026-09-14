using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unswarm.Api.BackgroundServices;
using Unswarm.Core.Models;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Unswarm.Core.Persistence;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="StuckStateRecoveryService"/>.
/// Uses an in-memory SQLite database to verify stuck-state recovery logic.
/// </summary>
public sealed class StuckStateRecoveryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public StuckStateRecoveryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private Func<UnswarmDbContext> CreateDbFactory()
    {
        return () =>
        {
            var options = new DbContextOptionsBuilder<UnswarmDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new UnswarmDbContext(options);
        };
    }

    private static async Task RunServiceAsync(BackgroundService service, Func<Task> act, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await act();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(timeout ?? TimeSpan.FromSeconds(15));
            cts.Cancel();
        }
    }

    [Fact]
    public async Task ExecuteAsync_ResetsStuckStartingRuntimes()
    {
        var dbFactory = CreateDbFactory();
        using (var db = dbFactory())
        {
            db.Database.EnsureCreated();
            db.RegisteredRuntimes.Add(new RegisteredRuntimeEntity
            {
                Id = "stuck-1",
                DisplayName = "Stuck Model",
                Image = "stuck:latest",
                Status = "Starting",
                RuntimeProcessId = 42,
                ErrorMessage = "previous error",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton<Func<UnswarmDbContext>>(dbFactory);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var provider = services.BuildServiceProvider();

        var service = new StuckStateRecoveryService(
            provider,
            new LoggerFactory().CreateLogger<StuckStateRecoveryService>());

        await RunServiceAsync(service, async () =>
        {
            // Wait for service to complete — it sleeps 3s then runs once
            await Task.Delay(5000);

            using var verify = dbFactory();
            var runtime = verify.RegisteredRuntimes.Single(r => r.Id == "stuck-1");
            Assert.Equal(nameof(ContainerRegistrationStatus.Registered), runtime.Status);
            Assert.Equal("Reset from stuck Starting state", runtime.ErrorMessage);
            Assert.Null(runtime.RuntimeProcessId);
        }, timeout: TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotTouchNonStartingRuntimes()
    {
        var dbFactory = CreateDbFactory();
        using (var db = dbFactory())
        {
            db.Database.EnsureCreated();
            db.RegisteredRuntimes.Add(new RegisteredRuntimeEntity
            {
                Id = "running-1",
                DisplayName = "Running Model",
                Image = "running:latest",
                Status = nameof(ContainerRegistrationStatus.Ready),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton<Func<UnswarmDbContext>>(dbFactory);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var provider = services.BuildServiceProvider();

        var service = new StuckStateRecoveryService(
            provider,
            new LoggerFactory().CreateLogger<StuckStateRecoveryService>());

        await RunServiceAsync(service, async () =>
        {
            await Task.Delay(5000);

            using var verify = dbFactory();
            var runtime = verify.RegisteredRuntimes.Single(r => r.Id == "running-1");
            Assert.Equal(nameof(ContainerRegistrationStatus.Ready), runtime.Status);
            Assert.Null(runtime.ErrorMessage);
        }, timeout: TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ExecuteAsync_HandlesNoStuckRuntimesGracefully()
    {
        var dbFactory = CreateDbFactory();
        using (var db = dbFactory())
        {
            db.Database.EnsureCreated();
        }

        var services = new ServiceCollection();
        services.AddSingleton<Func<UnswarmDbContext>>(dbFactory);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var provider = services.BuildServiceProvider();

        var service = new StuckStateRecoveryService(
            provider,
            new LoggerFactory().CreateLogger<StuckStateRecoveryService>());

        // Should not throw — runs with zero stuck runtimes
        await RunServiceAsync(service, async () =>
        {
            await Task.Delay(5000);

            using var verify = dbFactory();
            Assert.Empty(verify.RegisteredRuntimes);
        }, timeout: TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ExecuteAsync_ResetsMultipleStuckRuntimes()
    {
        var dbFactory = CreateDbFactory();
        using (var db = dbFactory())
        {
            db.Database.EnsureCreated();
            db.RegisteredRuntimes.AddRange(
                new RegisteredRuntimeEntity
                {
                    Id = "stuck-a",
                    DisplayName = "Model A",
                    Image = "a:latest",
                    Status = "Starting",
                    RuntimeProcessId = 11,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new RegisteredRuntimeEntity
                {
                    Id = "stuck-b",
                    DisplayName = "Model B",
                    Image = "b:latest",
                    Status = "Starting",
                    RuntimeProcessId = 22,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new RegisteredRuntimeEntity
                {
                    Id = "ok-c",
                    DisplayName = "Model C",
                    Image = "c:latest",
                    Status = nameof(ContainerRegistrationStatus.Ready),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton<Func<UnswarmDbContext>>(dbFactory);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var provider = services.BuildServiceProvider();

        var service = new StuckStateRecoveryService(
            provider,
            new LoggerFactory().CreateLogger<StuckStateRecoveryService>());

        await RunServiceAsync(service, async () =>
        {
            await Task.Delay(5000);

            using var verify = dbFactory();
            var all = verify.RegisteredRuntimes.OrderBy(r => r.Id).ToList();

            // stuck-a reset
            var a = all.Single(r => r.Id == "stuck-a");
            Assert.Equal(nameof(ContainerRegistrationStatus.Registered), a.Status);
            Assert.Null(a.RuntimeProcessId);

            // stuck-b reset
            var b = all.Single(r => r.Id == "stuck-b");
            Assert.Equal(nameof(ContainerRegistrationStatus.Registered), b.Status);
            Assert.Null(b.RuntimeProcessId);

            // ok-c untouched
            var c = all.Single(r => r.Id == "ok-c");
            Assert.Equal(nameof(ContainerRegistrationStatus.Ready), c.Status);
        }, timeout: TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ExecuteAsync_SetsUpdatedAtToUtcNow()
    {
        var dbFactory = CreateDbFactory();
        var beforeReset = DateTimeOffset.UtcNow;
        using (var db = dbFactory())
        {
            db.Database.EnsureCreated();
            db.RegisteredRuntimes.Add(new RegisteredRuntimeEntity
            {
                Id = "stuck-ts",
                DisplayName = "Timestamp Model",
                Image = "ts:latest",
                Status = "Starting",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton<Func<UnswarmDbContext>>(dbFactory);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var provider = services.BuildServiceProvider();

        var service = new StuckStateRecoveryService(
            provider,
            new LoggerFactory().CreateLogger<StuckStateRecoveryService>());

        await RunServiceAsync(service, async () =>
        {
            await Task.Delay(5000);

            using var verify = dbFactory();
            var runtime = verify.RegisteredRuntimes.Single(r => r.Id == "stuck-ts");
            Assert.True(runtime.UpdatedAt >= beforeReset,
                $"UpdatedAt {runtime.UpdatedAt} should be >= service start time {beforeReset}");
        }, timeout: TimeSpan.FromSeconds(20));
    }
}
