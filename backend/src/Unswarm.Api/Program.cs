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
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

// ── CLI args ───────────────────────────────────────────────────────────────
string? adminSetupPassword = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--admin-setup")
    {
        adminSetupPassword = args[i + 1];
        break;
    }
}

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
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;

        // User settings
        options.User.RequireUniqueEmail = false;
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<UnswarmDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<SignInManager<ApplicationUser>>();

// Register the authentication scheme so SignInAsync has a handler to call
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme);

// Cookie authentication for SPA
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".Unswarm.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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
builder.Services.AddSingleton<IApiKeyStore, ApiKeyStore>();
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

// ── Authorization policies (path-scoped API keys) ─────────────────────
// An API key carries an "unswarm:key-scope" claim set by ApiKeyAuthMiddleware.
// The control plane is gated by the cookie principal + roles; inference and
// agent surfaces are gated by their scope so a key can never cross surfaces.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Cookie", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("InferenceKey", policy =>
        policy.RequireClaim(ApiKeyAuthMiddleware.ScopeClaimType, ApiKeyScope.Inference.ToString()));
});

// ── Background services ──────────────────────────────────────────────────
builder.Services.AddHostedService<SchedulerHostedService>();
// builder.Services.AddHostedService<HealthCheckService>();   // disabled: proxy handles container lifecycle on-demand
// builder.Services.AddHostedService<IdleShutdownService>();   // disabled: proxy handles container lifecycle on-demand
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

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
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

    // Prompt default-flag column drift repair: existing installs that predate the
    // selectable-default feature need the IsDefault column added to their Prompts table.
    await EnsurePromptDefaultColumnAsync(db);

    // Prompt versioning schema drift repair: adds CurrentVersion column to Prompts,
    // creates the PromptVersions table, and backfills version-1 rows for existing prompts.
    await EnsurePromptVersioningAsync(db);

    // Benchmark history prompt-identity columns drift repair: adds PromptId, PromptName,
    // PromptVersion columns to the Benchmarks table for existing installs.
    await EnsureBenchmarkPromptIdentityColumnsAsync(db);

    // RegisteredContainers → RegisteredRuntimes column drift repair. Older installs
    // may have the RegisteredContainers table without the RuntimeKind/LauncherPath/
    // RuntimeProcessId columns (added when script runtimes were introduced), and may
    // still have a RegisteredContainerId column in ContainerModelMappings instead of
    // the renamed RegisteredRuntimeId. Idempotent — harmless on fresh DBs.
    await EnsureRuntimeColumnsAsync(db);

    // Identity tables: same pattern as above — EnsureCreated does nothing on existing DBs.
    await EnsureIdentityTablesAsync(db);
}

// ── Migrate the static API key into the managed key store ────────────
// The single configured key (UNSWARM_API_KEY / Auth.ApiKey) is the remote agent's
// credential for /api/agents and /ws/agent. Rather than keep it out-of-band, seed
// it as an agent-scoped managed row so it authenticates through the same store as
// newly generated keys. Idempotent.
await SeedStaticApiKeyAsync(app.Services);

// ── Seed roles and admin user ──────────────────────────────────────────
await SeedRolesAsync(app.Services);
await SeedAdminUserAsync(app.Services, adminSetupPassword);

// ── Adopt orphaned script processes ─────────────────────────────────────
// If the server restarts while host script runtimes are alive, their PID files
// survive on disk. Adopt them so IdleShutdown and StopScript can manage them.
await app.Services.GetRequiredService<HostScriptRuntimeController>()
    .AdoptOrphanedScriptsAsync();

// ── Middleware ────────────────────────────────────────────────────────────
app.UseCors("SpaCors");
app.UseAuthentication();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions
{
    AllowedOrigins = { "http://localhost:3000", "http://localhost:5173" }
});
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
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
/// SQLite-only, idempotent drift repair for the Prompts table: adds the IsDefault
/// column when it is missing (existing installs that predate the selectable-default
/// feature). Follows the same PRAGMA table_info + ALTER TABLE pattern used by
/// EnsureBenchmarkSchemaColumnsAsync. Non-fatal on failure.
/// </summary>
static async Task EnsurePromptDefaultColumnAsync(UnswarmDbContext db)
{
    try
    {
        const string table = "Prompts";
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();

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

        if (!columns.Contains("IsDefault", StringComparer.OrdinalIgnoreCase))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN \"IsDefault\" INTEGER NOT NULL DEFAULT 0";
            await cmd.ExecuteNonQueryAsync();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to add IsDefault column to Prompts table: {ex.Message}");
    }
    finally
    {
        if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            await db.Database.CloseConnectionAsync();
    }
}

/// <summary>
/// SQLite-only, idempotent drift repair for prompt versioning: adds the CurrentVersion
/// column to Prompts, creates the PromptVersions table with its unique index, and
/// backfills a version-1 row for every existing prompt that has no versions yet.
/// Non-fatal on failure — repairs on the next start.
/// </summary>
static async Task EnsurePromptVersioningAsync(UnswarmDbContext db)
{
    try
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();

        // ── 1. Prompts: add CurrentVersion column if missing ─────────────
        var promptColumns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"Prompts\")";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                promptColumns.Add(reader.GetString(1)); // column name
            }
        }

        if (!promptColumns.Contains("CurrentVersion", StringComparer.OrdinalIgnoreCase))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Prompts\" ADD COLUMN \"CurrentVersion\" INTEGER NOT NULL DEFAULT 1";
            await cmd.ExecuteNonQueryAsync();
        }

        // ── 2. PromptVersions: create table + unique index if missing ─────
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS "PromptVersions" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_PromptVersions" PRIMARY KEY,
                    "PromptId" TEXT NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "Text" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_PromptVersions_Prompts_PromptId" FOREIGN KEY ("PromptId") REFERENCES "Prompts" ("Id") ON DELETE CASCADE
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PromptVersions_PromptId_Version" ON "PromptVersions" ("PromptId", "Version");""";
            await cmd.ExecuteNonQueryAsync();
        }

        // ── 3. Backfill version-1 for prompts missing from PromptVersions ──
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO "PromptVersions" ("Id", "PromptId", "Version", "Text", "CreatedAt")
                SELECT lower(hex(randomblob(16))), p."Id", 1, p."Text", p."CreatedAt"
                FROM "Prompts" p
                WHERE NOT EXISTS (SELECT 1 FROM "PromptVersions" pv WHERE pv."PromptId" = p."Id")
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to ensure prompt versioning schema: {ex.Message}");
    }
    finally
    {
        if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            await db.Database.CloseConnectionAsync();
    }
}

/// <summary>
/// SQLite-only, idempotent drift repair for benchmark-history prompt identity:
/// adds PromptId, PromptName, PromptVersion columns to the Benchmarks table.
/// Non-fatal on failure — repairs on the next start.
/// </summary>
static async Task EnsureBenchmarkPromptIdentityColumnsAsync(UnswarmDbContext db)
{
    try
    {
        const string table = "Benchmarks";
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();

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
        if (!columns.Contains("PromptId", StringComparer.OrdinalIgnoreCase))
            adds.Add("ADD COLUMN \"PromptId\" TEXT NULL");
        if (!columns.Contains("PromptName", StringComparer.OrdinalIgnoreCase))
            adds.Add("ADD COLUMN \"PromptName\" TEXT NULL");
        if (!columns.Contains("PromptVersion", StringComparer.OrdinalIgnoreCase))
            adds.Add("ADD COLUMN \"PromptVersion\" INTEGER NULL");

        foreach (var add in adds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} {add}";
            await cmd.ExecuteNonQueryAsync();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unswarm startup: failed to repair benchmark history prompt identity schema: {ex.Message}");
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

static async Task SeedAdminUserAsync(IServiceProvider services, string? adminSetupPassword)
{
    using var scope = services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    var existingAdmin = await userManager.FindByNameAsync("admin");

    // No --admin-setup flag: skip (print instructions if no admin exists)
    if (string.IsNullOrWhiteSpace(adminSetupPassword))
    {
        if (existingAdmin == null)
        {
            Console.WriteLine("No admin user exists. Run with --admin-setup <password> to create one:");
            Console.WriteLine("  unswarm --admin-setup 'your-password'");
        }
        return;
    }

    // Admin exists — reset their password
    if (existingAdmin != null)
    {
        var token = await userManager.GeneratePasswordResetTokenAsync(existingAdmin);
        var result = await userManager.ResetPasswordAsync(existingAdmin, token, adminSetupPassword);
        if (result.Succeeded)
        {
            existingAdmin.IsTempPassword = true;
            await userManager.UpdateAsync(existingAdmin);
            Console.WriteLine("Admin password has been reset.");
        }
        else
        {
            Console.Error.WriteLine($"Failed to reset admin password: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }
        return;
    }

    // First run — create admin user
    var admin = new ApplicationUser
    {
        UserName = "admin",
        IsTempPassword = true
    };

    var createResult = await userManager.CreateAsync(admin, adminSetupPassword);
    if (createResult.Succeeded)
    {
        await userManager.AddToRoleAsync(admin, "Admin");
        Console.WriteLine("Created admin user (username: admin). Change the password immediately.");
    }
    else
    {
        Console.Error.WriteLine($"Failed to create admin user: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
    }
}

/// <summary>
/// Seed the configured static API key (UNSWARM_API_KEY / Auth.ApiKey) into the
/// managed key store as an agent-scoped key, so the remote agent authenticates
/// through the same store as generated keys. Idempotent: skips if a key matching
/// the configured secret already exists.
/// </summary>
static async Task SeedStaticApiKeyAsync(IServiceProvider services)
{
    var config = services.GetRequiredService<IConfiguration>();
    string? envKey = Environment.GetEnvironmentVariable("UNSWARM_API_KEY");
    string staticKey = (!string.IsNullOrWhiteSpace(envKey) ? envKey
        : (config["Auth:ApiKey"] ?? string.Empty).Trim());

    if (string.IsNullOrWhiteSpace(staticKey))
        return;

    var store = services.GetRequiredService<IApiKeyStore>();
    if (await store.AuthenticateAsync(staticKey) is not null)
        return; // already seeded

    await store.CreateAsync(
        "Static API key (UNSWARM_API_KEY / Auth.ApiKey)",
        ApiKeyScope.Agent,
        staticKey);
    Console.WriteLine("Seeded static API key into managed key store (agent scope).");
}

/// <summary>
/// Adds security headers: Content-Security-Policy, X-Content-Type-Options,
/// X-Frame-Options, Referrer-Policy, and Permissions-Policy.
/// </summary>
sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'";

        await next(context);
    }
}
