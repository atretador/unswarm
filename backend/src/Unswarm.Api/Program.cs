using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Unswarm.Api.Configuration;
using Unswarm.Api.Middleware;
using Unswarm.Api.BackgroundServices;
using Unswarm.Api.Services;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Core.Services.Validation;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Unswarm.Core.Telemetry;

// ── CLI args ───────────────────────────────────────────────────────────────
string? adminSetupPassword = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--admin-setup")
    {
        adminSetupPassword = args[i + 1];
        // Passing secrets via argv leaks them to `ps` and shell history —
        // prefer the UNSWARM_ADMIN_PASSWORD environment variable.
        Console.WriteLine("Warning: --admin-setup passes the admin password via argv, which is visible in process listings and shell history. Prefer the UNSWARM_ADMIN_PASSWORD environment variable instead.");
        break;
    }
}

// Environment-variable alternative to --admin-setup (argv wins if both set).
adminSetupPassword ??= Environment.GetEnvironmentVariable("UNSWARM_ADMIN_PASSWORD") is { Length: > 0 } envPassword
    ? envPassword
    : null;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".config", "unswarm", "unswarm.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";

// Shared app data directory (~/.config/unswarm; /data/.config/unswarm in the
// container, where HOME is pointed at the mounted volume). The DataProtection
// key ring lives here too so persisted API-key ciphertexts survive restarts.
var appDataDir = Path.GetDirectoryName(dbPath)!;
builder.Services.AddDbContext<UnswarmDbContext>(options =>
    options.UseSqlite(connectionString)
           .AddInterceptors(SqliteTuningInterceptor.Instance));

// Factory for services that manage their own DbContext lifetime (singleton services)
builder.Services.AddSingleton<Func<UnswarmDbContext>>(sp =>
{
    return () =>
    {
        var optionsBuilder = new DbContextOptionsBuilder<UnswarmDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(SqliteTuningInterceptor.Instance);
        return new UnswarmDbContext(optionsBuilder.Options);
    };
});

// ── Data Protection ───────────────────────────────────────────────────────
// Persist the key ring under the app data dir (<datadir>/keys). Without this,
// keys are regenerated on every restart and previously encrypted API-key
// ciphertexts (CloudProviderStore, ApiKeyStore) become undecryptable. In the
// container HOME=/data and the unswarm-data volume keeps <datadir>/keys across
// container recreation.
var dataProtectionKeyDir = Path.Combine(appDataDir, "keys");
Directory.CreateDirectory(dataProtectionKeyDir);
builder.Services.AddDataProtection()
    // Stable application discriminator: without this it derives from the
    // content-root path, so host runs and container runs would each reject
    // the other's ciphertexts even when sharing the same key directory.
    .SetApplicationName("unswarm")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyDir));

builder.Services.AddHttpContextAccessor();

// ── Identity + Auth ────────────────────────────────────────────────────────
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Password settings (relaxed for self-hosted, but with a sane minimum)
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 10;
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
        // Singleton: SettingsStore is stateless (Func<UnswarmDbContext> holder, fresh
        // DbContext per call) and is consumed by singletons (SchedulerWorker, global
        // channel factory). Scoped registration would fail DI scope validation.
        builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
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
builder.Services.AddScoped<IUsageRecorder, UsageRecorder>();
// Per-key model access control for the /v1 inference surface. Shares the API key
// store so access rules are served from its hot-path cache (invalidated on save).
builder.Services.AddScoped<IApiKeyAccessService>(sp => new ApiKeyAccessService(
    sp.GetRequiredService<Func<UnswarmDbContext>>(),
    sp.GetRequiredService<IContainerRegistry>(),
    sp.GetRequiredService<ILogger<ApiKeyAccessService>>(),
    sp.GetRequiredService<IApiKeyStore>()));
// Singleton: usage records are fanned out to /ws/metrics live-tail subscribers
// from UsageRecorder's throwaway scopes.
builder.Services.AddSingleton<IUsageLiveTailBroadcaster, UsageLiveTailBroadcaster>();

// Cloud providers: scoped store (Func<UnswarmDbContext> holder, like other scoped stores)
builder.Services.AddSingleton<IApiKeyEncryptor, DataProtectionEncryptor>();
builder.Services.AddScoped<ICloudProviderStore, CloudProviderStore>();

// Cloud forwarding: singleton (global SemaphoreSlim concurrency cap) with
// IServiceScopeFactory to resolve scoped ICloudProviderStore per request.
builder.Services.AddSingleton<ICloudForwardingService, CloudForwardingService>();

// ── HTTP Client for cloud providers ──────────────────────────────────────
// Dedicated named client with infinite timeout for long-running upstream streams.
// Cancellation is driven by the request's CancellationToken so client disconnect
// cancels the upstream call (stops token spend).
builder.Services.AddHttpClient("cloud-provider")
    .ConfigureHttpClient(c =>
    {
        c.Timeout = Timeout.InfiniteTimeSpan;
    });

// ── Scheduler ─────────────────────────────────────────────────────────────
// Global bounded channel: depth loaded from settings at first resolution.
// Sync-over-async (GetAwaiter().GetResult()) is safe here because the
// DbContext factory uses an in-process SQLite database with no real I/O.
builder.Services.AddSingleton(sp =>
{
    var settingsStore = sp.GetRequiredService<ISettingsStore>();
    // Load settings synchronously at startup — safe for in-process SQLite.
    var settings = settingsStore.GetAsync().GetAwaiter().GetResult();
    var depth = Math.Clamp(settings.MaxQueueDepth, 1, 10000);
    return Channel.CreateBounded<InferenceRequest>(
        new BoundedChannelOptions(depth) { FullMode = BoundedChannelFullMode.Wait });
});
builder.Services.AddSingleton<SchedulerSettings>();
builder.Services.AddSingleton<SchedulerWorker>();
builder.Services.AddSingleton<ISchedulerQueue, SchedulerQueue>();
builder.Services.AddSingleton<ISchedulerDrainer>(sp => sp.GetRequiredService<SchedulerWorker>());

// ── Auto-benchmark ────────────────────────────────────────────────────────
// Singleton so it can be captured by ContainerRegistrationService's fire-and-forget
// background runner (which outlives the request scope that triggered registration).
// The scoped stores it depends on are stateless Func<UnswarmDbContext> holders, so
// resolving them once from a scope here is safe for long-lived background use.
builder.Services.AddSingleton<Unswarm.Core.Services.Benchmarks.AutoBenchmarkService>(sp =>
{
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    using var scope = scopeFactory.CreateScope();
    return new Unswarm.Core.Services.Benchmarks.AutoBenchmarkService(
        scope.ServiceProvider.GetRequiredService<ISettingsStore>(),
        scope.ServiceProvider.GetRequiredService<IPromptStore>(),
        sp.GetRequiredService<ISchedulerQueue>(),
        scope.ServiceProvider.GetRequiredService<IBenchmarkHistory>(),
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<ILogStore>(),
        sp.GetRequiredService<ILogger<Unswarm.Core.Services.Benchmarks.AutoBenchmarkService>>());
});

// ── OpenTelemetry ─────────────────────────────────────────────────────────
// Traces + metrics for ASP.NET Core and HttpClient, plus Unswarm's custom
// "Unswarm" meter. OTLP export is enabled only when OTEL_EXPORTER_OTLP_ENDPOINT
// is set; Prometheus scraping is always available at /metrics. With no exporter
// configured everything stays in-process and cheap (no-op instruments).
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: builder.Environment.ApplicationName ?? "Unswarm.Api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
        }
    });

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: builder.Environment.ApplicationName ?? "Unswarm.Api"))
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddMeter(UnswarmMetrics.MeterName)
               .AddPrometheusExporter();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter((options, metricReaderOptions) =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
        }
    });

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
    options.AddPolicy("AgentKey", policy =>
        policy.RequireClaim(ApiKeyAuthMiddleware.ScopeClaimType, ApiKeyScope.Agent.ToString()));
});

// ── Background services ──────────────────────────────────────────────────
builder.Services.AddHostedService<SchedulerHostedService>();
// builder.Services.AddHostedService<HealthCheckService>();   // disabled: proxy handles container lifecycle on-demand
builder.Services.AddHostedService<IdleShutdownService>();
builder.Services.AddHostedService<LogRetentionService>();
builder.Services.AddHostedService<ContainerLogProbe>();

// ── Global exception handling ─────────────────────────────────────────────
// Unhandled exceptions become RFC7807 ProblemDetails instead of an empty 500.
builder.Services.AddExceptionHandler(_ => { });
builder.Services.AddProblemDetails();

// ── Health checks ─────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

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
    {
        // /v1 inference endpoints: higher limit, keyed by API key ID
        if (context.Request.Path.StartsWithSegments("/v1"))
        {
            var apiKeyId = context.User.FindFirst("unswarm:key-id")?.Value ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"apikey:{apiKeyId}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 600,
                    QueueLimit = 0,
                    Window = TimeSpan.FromMinutes(1)
                });
        }

        // Management endpoints: standard per-IP rate limit
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"ip:{context.Connection.RemoteIpAddress}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            });
    });
});

// ── Swagger / OpenAPI ──────────────────────────────────────────────────────
// Auto-generated OpenAPI 3.0 spec + Swagger UI from controllers and DTOs.
// Available at /swagger (UI) and /swagger/v1/swagger.json (spec).
// Exposed in all environments so self-hosted deployments get docs out of the box.
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Unswarm API",
        Version = "1.0",
        Description = "Self-hosted control plane for managing LLM inference infrastructure. " +
            "The /v1 surface is an OpenAI-compatible proxy; /api/* is the management REST API."
    });

    // Include the XML documentation file so method/parameter descriptions appear in Swagger.
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath);

    // API key security scheme (Bearer token used by /v1 and /api/*).
    options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Description = "API key authentication. Prefix with 'Bearer ': Authorization: Bearer <key>"
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ── HSTS (non-development only) ──────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// ── Initialize database ──────────────────────────────────────────────────
// EF Core migrations: applies pending migrations (creating __EFMigrationsHistory
// and the full schema on a fresh DB). Replaces the old EnsureCreated + PRAGMA
// drift-repair approach; pre-release, old dev DB files are simply disposable.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UnswarmDbContext>();
    await db.Database.MigrateAsync();
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
// Early: unhandled exceptions anywhere in the pipeline become RFC7807
// ProblemDetails instead of an empty 500.
app.UseExceptionHandler();

app.UseCors("SpaCors");
app.UseAuthentication();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions
{
    AllowedOrigins = { "http://localhost:3000", "http://localhost:5173" }
});
app.UseMiddleware<SecurityHeadersMiddleware>();

// ── Swagger ────────────────────────────────────────────────────────────────
// Serves the auto-generated OpenAPI spec JSON.
app.UseSwagger();

// ── Prometheus /metrics scrape protection ─────────────────────────────────
// The endpoint itself stays AllowAnonymous (scrapers can't do the cookie
// dance); this guard decides who may read it:
//   - Prometheus:ScrapeToken set (env PROMETHEUS_SCRAPE_TOKEN): require
//     "Authorization: Bearer <token>", 401 otherwise.
//   - Unset: loopback-only (127.0.0.1 / ::1), 403 for everyone else.
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value?.Equals("/metrics", StringComparison.OrdinalIgnoreCase) == true)
    {
        var scrapeToken = Environment.GetEnvironmentVariable("PROMETHEUS_SCRAPE_TOKEN");
        if (string.IsNullOrWhiteSpace(scrapeToken))
            scrapeToken = context.RequestServices.GetRequiredService<IConfiguration>()["Prometheus:ScrapeToken"];

        if (!string.IsNullOrWhiteSpace(scrapeToken))
        {
            var presented = context.Request.Headers.Authorization.ToString();
            var presentedToken = presented.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? presented["Bearer ".Length..].Trim()
                : string.Empty;
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(presentedToken),
                    System.Text.Encoding.UTF8.GetBytes(scrapeToken)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        else
        {
            var remote = context.Connection.RemoteIpAddress;
            var isLoopback = remote is not null && IPAddress.IsLoopback(remote); // covers 127.0.0.1 and ::1
            if (!isLoopback)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
    }

    await next(context);
});
app.MapControllers();

// Anonymous liveness probe — deliberately outside any auth surface
// ("/health" is not a protected prefix in ApiKeyAuthMiddleware).
app.MapHealthChecks("/health");

// Prometheus scrape endpoint — access is gated by the scrape-protection
// middleware above ("/metrics" is not a protected prefix in
// ApiKeyAuthMiddleware). Serves whatever the OpenTelemetry metric provider has
// collected, including the "Unswarm" meter.
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();

// Swagger UI — interactive API docs. Anonymous so unauthenticated users can explore.
app.UseSwaggerUI(options => {
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Unswarm API v1");
});

app.Run();

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
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: blob:; connect-src 'self'";

        await next(context);
    }
}

// Marker for WebApplicationFactory<Program> (E2E tests).
public partial class Program { }
