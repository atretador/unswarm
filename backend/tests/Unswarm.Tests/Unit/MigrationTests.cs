using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Proves the migration-based startup path: a fresh <c>MigrateAsync</c> against a
/// real SQLite file database creates the full schema (including __EFMigrationsHistory)
/// and that the schema is usable by the app's seeding flows (roles + admin user),
/// mirroring what Program.cs does after <c>MigrateAsync</c>.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dbPath;

    public MigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"unswarm-migration-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _dbPath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private DbContextOptions<UnswarmDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

    [Fact]
    public async Task MigrateAsync_OnFreshDb_CreatesFullSchemaAndHistory()
    {
        // Act: fresh migrate on an empty file DB (same as Program.cs startup).
        await using (var db = new UnswarmDbContext(CreateOptions()))
        {
            await db.Database.MigrateAsync();
        }

        // Assert: migrations history table exists and records the initial migration.
        await using var check = new UnswarmDbContext(CreateOptions());
        var conn = (SqliteConnection)check.Database.GetDbConnection();
        await conn.OpenAsync();

        Assert.True(await TableExistsAsync(conn, "__EFMigrationsHistory"));
        var applied = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260822065100_InitialCreate'");
        Assert.Equal(1L, applied);

        // Assert: representative tables from every subsystem exist.
        foreach (var table in new[]
                 {
                     "AspNetRoles", "AspNetUsers", "Models", "Benchmarks", "Logs",
                     "Settings", "RegisteredContainers", "ContainerModelMappings",
                     "Prompts", "PromptVersions", "ApiKeys"
                 })
        {
            Assert.True(await TableExistsAsync(conn, table), $"missing table: {table}");
        }

        // Assert: columns that previously required PRAGMA drift repair exist from the start.
        Assert.True(await ColumnExistsAsync(conn, "Prompts", "IsDefault"));
        Assert.True(await ColumnExistsAsync(conn, "Prompts", "CurrentVersion"));
        Assert.True(await ColumnExistsAsync(conn, "Benchmarks", "PromptId"));
        Assert.True(await ColumnExistsAsync(conn, "RegisteredContainers", "RuntimeKind"));
    }

    [Fact]
    public async Task MigrateAsync_SecondRun_IsIdempotent()
    {
        await using (var db = new UnswarmDbContext(CreateOptions()))
        {
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync(); // no pending migrations — must not throw
        }
    }

    [Fact]
    public async Task MigrateAsync_SchemaSupportsAppSeeding()
    {
        // Arrange: fresh migrated DB.
        await using (var db = new UnswarmDbContext(CreateOptions()))
        {
            await db.Database.MigrateAsync();
        }

        // Act: run the same seeding flow Program.cs performs after MigrateAsync
        // (roles + admin user) through real Identity managers over the migrated DB.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<UnswarmDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 6;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<UnswarmDbContext>()
            .AddDefaultTokenProviders();

        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Admin", "User" })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var createResult = await userManager.CreateAsync(
            new ApplicationUser { UserName = "admin", IsTempPassword = true }, "test-password");
        Assert.True(createResult.Succeeded);
        await userManager.AddToRoleAsync((await userManager.FindByNameAsync("admin"))!, "Admin");

        // Assert: seeding persisted to the migrated database.
        Assert.True(await roleManager.RoleExistsAsync("Admin"));
        var admin = await userManager.FindByNameAsync("admin");
        Assert.NotNull(admin);
        Assert.True(admin!.IsTempPassword);
        Assert.True(await userManager.CheckPasswordAsync(admin, "test-password"));
        Assert.True(await userManager.IsInRoleAsync(admin, "Admin"));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table)
    {
        var count = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = @table",
            ("@table", table));
        return count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
