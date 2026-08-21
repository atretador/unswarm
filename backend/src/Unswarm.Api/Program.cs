using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
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

builder.Services.AddDataProtection();
builder.Services.AddHttpContextAccessor();

// ── Identity + Auth ────────────────────────────────────────────────────────
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Password settings (relaxed for self-hosted)
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
        options.Password.RequiredUniqueChars = 1;

        // Lockout settings
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.MaxFailedAccessAttempts = 10;
        options.Lockout.AllowedForNewUsers = true;

        // User settings
        options.User.RequireUniqueEmail = false;
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<UnswarmDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<SignInManager<ApplicationUser>>();

// Cookie authentication for SPA
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".Unswarm.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;

    // For SPA: return 401/403 instead of redirecting to login
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

// API key auth (backward compat with agent WebSocket connections)
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
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
builder.Services.AddScoped<IBenchmarkHistory, BenchmarkHistoryService>();
builder.Services.AddScoped<IPromptStore, PromptStore>();
builder.Services.AddSingleton<IContainerRegistry, ContainerRegistry>();
builder.Services.AddSingleton<HostScriptRuntimeController>();
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
        // Frontend wire contract for statuses is lowercase (AgentsController
        // ToContainerStatus precedent). CamelCase aligns all enum-typed DTO statuses
        // (ModelStatus, ContainerStatus, QueueItemStatus) with the frontend types.
        // JsonStringEnumConverter still READS case-insensitively, so request bodies
        // with either casing keep working.
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

// ── Background services ──────────────────────────────────────────────────
builder.Services.AddHostedService<SchedulerHostedService>();
builder.Services.AddHostedService<HealthCheckService>();
builder.Services.AddHostedService<IdleShutdownService>();
builder.Services.AddHostedService<LogRetentionService>();

// ── CORS ──────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("SpaCors", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000", "http://localhost:5173"];
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

// ── Initialize database ──────────────────────────────────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UnswarmDbContext>();
    await db.Database.EnsureCreatedAsync();

    // Schema drift repair (EnsureCreated-only, no migrations): existing installs may
    // have an EMPTY BenchmarkHistory table with the OLD schema (no Prompt/
    // TokensGenerated/Status/ErrorMessage). Add missing columns idempotently.
    await EnsureBenchmarkSchemaColumnsAsync(db);

    // Model-status heal: the old smoke-validation path could strand rows in
    // 'Validating' forever (a busy/cancelled smoke never flipped them). Discovery is
    // now the sole validation, so any leftover 'Validating' row is treated as Ready.
    await HealStrandedValidatingModelsAsync(db);

    // Prompt library table: EnsureCreated only creates when the DB is entirely new.
    // Existing installs that upgrade past the P9 cutoff need this table created here.
    await EnsurePromptsTableAsync(db);

    // RegisteredContainers → RegisteredRuntimes column drift repair. Older installs
    // may have the RegisteredContainers table without the RuntimeKind/LauncherPath/
    // RuntimeProcessId columns (added when script runtimes were introduced), and may
    // still have a RegisteredContainerId column in ContainerModelMappings instead of
    // the renamed RegisteredRuntimeId. Idempotent — harmless on fresh DBs.
    await EnsureRuntimeColumnsAsync(db);

    // Identity tables: same pattern as above — EnsureCreated does nothing on existing DBs.
    await EnsureIdentityTablesAsync(db);
}

// ── Seed roles and admin user ──────────────────────────────────────────
await SeedRolesAsync(app.Services);
await SeedAdminUserAsync(app.Services);

// ── Adopt orphaned script processes ─────────────────────────────────────
// If the server restarts while host script runtimes are alive, their PID files
// survive on disk. Adopt them so IdleShutdown and StopScript can manage them.
await app.Services.GetRequiredService<HostScriptRuntimeController>()
    .AdoptOrphanedScriptsAsync();

// ── Middleware ────────────────────────────────────────────────────────────
app.UseCors("SpaCors");
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.MapControllers();

app.Run();

/// <summary>
/// SQLite-only, idempotent schema repair for the BenchmarkHistory table. New columns
/// added since the original EnsureCreated schema are added via ALTER TABLE when the
/// existing table is missing them, and a stale UNIQUE index on ModelId (from the old
/// 1:1 LastBenchmark nav) is dropped so multiple benchmark rows per model can be
/// written. Any failure is logged and swallowed so a startup problem never bricks
/// the API (the columns/index will be repaired on the next start).
/// </summary>
static async Task EnsureBenchmarkSchemaColumnsAsync(UnswarmDbContext db)
{
    try
    {
        // EF names the table after the DbSet ("Benchmarks"), not the entity class
        // (BenchmarkHistoryEntity). Using the wrong name makes every PRAGMA/ALTER
        // fail with "no such table" and the repair silently no-ops on old DBs.
        const string table = "Benchmarks";
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();

        // Read columns first.
        var columns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1)); // column name
            }
        }

        var adds = new List<string>();
        if (!columns.Contains("Prompt", StringComparer.OrdinalIgnoreCase))
            adds.Add("ADD COLUMN Prompt TEXT NULL");
        if (!columns.Contains("TokensGenerated", StringComparer.OrdinalIgnoreCase))
            adds.Add("ADD COLUMN TokensGenerated INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("Status", StringComparer.OrdinalIgnoreCase))
            adds.Add("ADD COLUMN Status TEXT NOT NULL DEFAULT 'completed'");
        if (!columns.Contains("ErrorMessage", StringComparer.OrdinalIgnoreCase))
            adds.Add("ADD COLUMN ErrorMessage TEXT NULL");

        foreach (var add in adds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} {add}";
            await cmd.ExecuteNonQueryAsync();
        }

        // Cleanup stale NULL-ModelId rows from old-schema installs.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"DELETE FROM {table} WHERE ModelId IS NULL OR ModelId = ''";
            await cmd.ExecuteNonQueryAsync();
        }

        // Drop any UNIQUE index on ModelId that the old 1:1 LastBenchmark nav created
        // (it prevents the benchmark-history semantics: many rows per model).
        var uniqueIndexes = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list({table})";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var unique = reader.GetInt32(2) == 1; // "unique" column
                if (unique)
                {
                    uniqueIndexes.Add(reader.GetString(1)); // index name
                }
            }
        }

        foreach (var indexName in uniqueIndexes)
        {
            // Only drop indexes that cover the ModelId column (the old nav's unique
            // constraint); skip the ordinary non-unique ModelId index if any.
            if (!await IndexCoversModelIdAsync(conn, indexName).ConfigureAwait(false))
                continue;

            Console.WriteLine($"Benchmarks schema repair: dropping stale unique index '{indexName}' on ModelId");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP INDEX IF EXISTS \"{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            await cmd.ExecuteNonQueryAsync();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to repair BenchmarkHistory schema: {ex.Message}");
    }
    finally
    {
        if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            await db.Database.CloseConnectionAsync();
    }
}

static async Task<bool> IndexCoversModelIdAsync(System.Data.Common.DbConnection conn, string indexName)
{
    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA index_info(\"{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var columnName = reader.GetString(2); // "name" column of the indexed column
            if (string.Equals(columnName, "ModelId", StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }
    catch
    {
        // Best-effort: if we cannot inspect the index, conservatively leave it alone.
    }
    return false;
}

/// <summary>
/// SQLite-only, idempotent heal for models stranded in 'Validating'. The old
/// registration flow ran a smoke chat-completion after discovery; when the server was
/// busy or the registration request was cancelled, that smoke hung and the already-
/// persisted model row never flipped to Ready/Invalid. Discovery is now the sole
/// validation, so any leftover 'Validating' row is flipped to 'Ready'. Non-fatal on
/// failure (the row is healed on the next start).
/// </summary>
static async Task HealStrandedValidatingModelsAsync(UnswarmDbContext db)
{
    try
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "UPDATE Models SET Status = 'Ready' WHERE Status = 'Validating'";
        await db.Database.OpenConnectionAsync();
        var affected = await cmd.ExecuteNonQueryAsync();
        if (affected > 0)
        {
            Console.WriteLine($"Healed {affected} stranded model(s) stuck in Validating → Ready");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to heal stranded Validating models: {ex.Message}");
    }
    finally
    {
        if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            await db.Database.CloseConnectionAsync();
    }
}

/// <summary>
/// SQLite-only, idempotent drift repair for the Prompts table added in P9. EnsureCreated
/// only runs when the DB is entirely new; existing installs that upgraded past P9 need
/// this CREATE TABLE IF NOT EXISTS to get the table created.
/// </summary>
static async Task EnsurePromptsTableAsync(UnswarmDbContext db)
{
    try
    {
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "Prompts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Prompts" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Text" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to create Prompts table: {ex.Message}");
    }
    finally
    {
        if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            await db.Database.CloseConnectionAsync();
    }
}

/// <summary>
/// SQLite-only, idempotent drift repair for the RegisteredContainers → RegisteredRuntimes
/// schema evolution. Adds missing columns (RuntimeKind, LauncherPath, RuntimeProcessId)
/// and renames RegisteredContainerId → RegisteredRuntimeId in ContainerModelMappings.
/// Non-fatal on failure — repairs on the next start.
/// </summary>
static async Task EnsureRuntimeColumnsAsync(UnswarmDbContext db)
{
    try
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();

        // ── 1. RegisteredContainers: add columns if missing ──────────────
        var rcColumns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"RegisteredContainers\")";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rcColumns.Add(reader.GetString(1)); // column name
            }
        }

        var rcAdds = new List<string>();
        if (!rcColumns.Contains("RuntimeKind", StringComparer.OrdinalIgnoreCase))
            rcAdds.Add("ADD COLUMN \"RuntimeKind\" TEXT NOT NULL DEFAULT 'Container'");
        if (!rcColumns.Contains("LauncherPath", StringComparer.OrdinalIgnoreCase))
            rcAdds.Add("ADD COLUMN \"LauncherPath\" TEXT NULL");
        if (!rcColumns.Contains("RuntimeProcessId", StringComparer.OrdinalIgnoreCase))
            rcAdds.Add("ADD COLUMN \"RuntimeProcessId\" INTEGER NULL");

        foreach (var add in rcAdds)
        {
            Console.WriteLine($"RegisteredContainers schema repair: {add}");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE \"RegisteredContainers\" {add}";
            await cmd.ExecuteNonQueryAsync();
        }

        // ── 2. ContainerModelMappings: rename RegisteredContainerId → RegisteredRuntimeId ──
        var cmmColumns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"ContainerModelMappings\")";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cmmColumns.Add(reader.GetString(1));
            }
        }

        if (cmmColumns.Contains("RegisteredContainerId", StringComparer.OrdinalIgnoreCase)
            && !cmmColumns.Contains("RegisteredRuntimeId", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("ContainerModelMappings schema repair: RENAME COLUMN RegisteredContainerId → RegisteredRuntimeId");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"ContainerModelMappings\" RENAME COLUMN \"RegisteredContainerId\" TO \"RegisteredRuntimeId\"";
            await cmd.ExecuteNonQueryAsync();
        }

        // ── 3. Models: rename SourceContainerId → SourceRuntimeId ──
        var modelColumns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"Models\")";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                modelColumns.Add(reader.GetString(1));
            }
        }

        if (modelColumns.Contains("SourceContainerId", StringComparer.OrdinalIgnoreCase)
            && !modelColumns.Contains("SourceRuntimeId", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Models schema repair: RENAME COLUMN SourceContainerId → SourceRuntimeId");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Models\" RENAME COLUMN \"SourceContainerId\" TO \"SourceRuntimeId\"";
            await cmd.ExecuteNonQueryAsync();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to repair runtime schema: {ex.Message}");
    }
    finally
    {
        if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            await db.Database.CloseConnectionAsync();
    }
}

/// <summary>
/// Creates the ASP.NET Core Identity tables if they don't exist.
/// EnsureCreated only creates tables when the DB is brand new; existing installs
/// need this idempotent CREATE TABLE IF NOT EXISTS for each Identity table.
/// </summary>
static async Task EnsureIdentityTablesAsync(UnswarmDbContext db)
{
    try
    {
        await db.Database.OpenConnectionAsync();
        var conn = db.Database.GetDbConnection();

        var statements = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "AspNetRoles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetRoles" PRIMARY KEY,
                "Name" TEXT(256) NULL,
                "NormalizedName" TEXT(256) NULL,
                "ConcurrencyStamp" TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "AspNetUsers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetUsers" PRIMARY KEY,
                "UserName" TEXT(256) NULL,
                "NormalizedUserName" TEXT(256) NULL,
                "Email" TEXT(256) NULL,
                "NormalizedEmail" TEXT(256) NULL,
                "EmailConfirmed" INTEGER NOT NULL DEFAULT 0,
                "PasswordHash" TEXT NULL,
                "SecurityStamp" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL,
                "PhoneNumber" TEXT NULL,
                "PhoneNumberConfirmed" INTEGER NOT NULL DEFAULT 0,
                "TwoFactorEnabled" INTEGER NOT NULL DEFAULT 0,
                "LockoutEnd" TEXT NULL,
                "LockoutEnabled" INTEGER NOT NULL DEFAULT 0,
                "AccessFailedCount" INTEGER NOT NULL DEFAULT 0,
                "IsTempPassword" INTEGER NOT NULL DEFAULT 0
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "AspNetRoleClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY AUTOINCREMENT,
                "RoleId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "AspNetUserClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "AspNetUserLogins" (
                "LoginProvider" TEXT NOT NULL,
                "ProviderKey" TEXT NOT NULL,
                "ProviderDisplayName" TEXT NULL,
                "UserId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey"),
                CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "AspNetUserRoles" (
                "UserId" TEXT NOT NULL,
                "RoleId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId"),
                CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "AspNetUserTokens" (
                "UserId" TEXT NOT NULL,
                "LoginProvider" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Value" TEXT NULL,
                CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name"),
                CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");""",
            """CREATE INDEX IF NOT EXISTS "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");""",
            """CREATE INDEX IF NOT EXISTS "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");""",
            """CREATE INDEX IF NOT EXISTS "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");""",
            """CREATE INDEX IF NOT EXISTS "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName");""",
        };

        foreach (var sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to ensure Identity tables: {ex.Message}");
    }
    finally
    {
        if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            await db.Database.CloseConnectionAsync();
    }
}

static async Task SeedRolesAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    string[] roles = ["Admin", "User"];
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }
}

static async Task SeedAdminUserAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    if (userManager.Users.Any())
        return;

    var defaultPassword = Environment.GetEnvironmentVariable("UNSWARM_ADMIN_PASSWORD") ?? "admin";
    var admin = new ApplicationUser
    {
        UserName = "admin",
        IsTempPassword = true
    };

    var result = await userManager.CreateAsync(admin, defaultPassword);
    if (result.Succeeded)
    {
        await userManager.AddToRoleAsync(admin, "Admin");
        Console.WriteLine("Created default admin user (username: admin). Change the password immediately.");
    }
    else
    {
        Console.Error.WriteLine($"Failed to seed admin user: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }
}
