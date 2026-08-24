using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Benchmarks;

namespace Unswarm.Core.Persistence;

public sealed class ModelEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string ParameterSize { get; set; } = string.Empty;
    public string Quantization { get; set; } = string.Empty;
    public string Status { get; set; } = nameof(ModelStatus.Validating);
    public int ContextWindow { get; set; }
    public string ContainerImage { get; set; } = string.Empty;
    public string? SourceRuntimeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public BenchmarkHistoryEntity? LastBenchmark { get; set; }
    public RegisteredRuntimeEntity? SourceRuntime { get; set; }
    public ICollection<ContainerModelMappingEntity> ContainerModelMappings { get; set; } = [];
}

public sealed class BenchmarkHistoryEntity
{
    public string Id { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public double TokensPerSec { get; set; }
    public double LatencyMs { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Prompt { get; set; }
    public long TokensGenerated { get; set; }
    public string Status { get; set; } = "completed";
    public string? ErrorMessage { get; set; }
    public string? PromptId { get; set; }
    public string? PromptName { get; set; }
    public int? PromptVersion { get; set; }

    /// <summary>
    /// Captured LLM response text (choices[0].message.content), truncated to a
    /// sane cap. Null when the body could not be read/parsed or the run errored.
    /// </summary>
    public string? Response { get; set; }

    /// <summary>
    /// Captured model reasoning/thinking text (choices[0].message.reasoning_content),
    /// truncated to the same sane cap as <see cref="Response"/>. Null when absent
    /// (non-thinking models) or when the body could not be read/parsed.
    /// </summary>
    public string? Reasoning { get; set; }
}

public sealed class LogEntity
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UtcTicks mirror of <see cref="Timestamp"/> as a plain long so SQLite can
    /// translate range comparisons (the provider cannot translate DateTimeOffset
    /// comparisons in WHERE clauses).
    /// </summary>
    public long TimestampTicks { get; set; }
    public string Level { get; set; } = nameof(LogLevel.Info);
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
}

public sealed class SettingsEntity
{
    public string Id { get; set; } = "default";
    public int RequestTimeout { get; set; } = 120;
    public int HealthCheckInterval { get; set; } = 10;
    public bool AutoShutdownIdle { get; set; } = false;
    public int IdleTimeout { get; set; } = 300;
    public int LogRetention { get; set; } = 168;
    public bool EnableBenchmarking { get; set; } = true;
    public string PriorityMode { get; set; } = "fifo";
    public bool BatchDrain { get; set; }
    public bool LazyStop { get; set; } = true;
    public int MaxQueueDepth { get; set; } = 32;
    public int MaxConcurrentTargets { get; set; }
    public int ParallelSlotSkipLimit { get; set; } = 3;
    public bool EnableParallelSlotSkip { get; set; }
    public int QueueStepsTillReset { get; set; } = 3;
    public bool EnableConversationAffinity { get; set; }
    public int ConversationDwellSeconds { get; set; } = 45;

    /// <summary>
    /// When true, hides the "cloud/" or "managed/" origin prefix from model display names.
    /// </summary>
    public bool HideOriginPrefix { get; set; }

    /// <summary>
    /// JSON map of agent names to user-chosen display names. E.g. {"host": "My Workstation"}.
    /// </summary>
    public string AgentDisplayNames { get; set; } = "{}";

    /// <summary>Usage records older than this many days are eligible for purge (0 = keep forever).</summary>
    public int UsageRetentionDays { get; set; } = 30;

    /// <summary>
    /// JSON map of provider name to monthly budget object, e.g.
    /// {"cloud":{"tokenBudget":1000000,"costBudget":25.0}}.
    /// </summary>
    public string ProviderBudgetsJson { get; set; } = "{}";
}

/// <summary>
/// EF entity for the RegisteredContainers physical table. The class is named
/// RegisteredRuntimeEntity to match the domain model rename, but the table
/// stays "RegisteredContainers" via the [Table] attribute so EnsureCreated
/// and existing data are unaffected.
/// </summary>
[Table("RegisteredContainers")]
public sealed class RegisteredRuntimeEntity
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Container name to interact with (pre-provisioned container).</summary>
    public string Image { get; set; } = string.Empty;
    public int ContainerPort { get; set; } = 8080;
    /// <summary>Discriminator: Container (default) or Script.</summary>
    public string RuntimeKind { get; set; } = "Container";
    /// <summary>Filesystem path to a host script (only set when RuntimeKind = Script).</summary>
    public string? LauncherPath { get; set; }
    /// <summary>Process id when a Script runtime is running (null for Container runtimes).</summary>
    public int? RuntimeProcessId { get; set; }
    public string? GpuDevices { get; set; }
    public long MemoryLimitMb { get; set; }
    public string ExtraLabelsJson { get; set; } = "{}";
    /// <summary>Execution target agent name; "host" for local Docker.</summary>
    public string Agent { get; set; } = "host";
    /// <summary>JSON array of same-agent container names this container may run concurrently with.</summary>
    public string CanRunAlongWithJson { get; set; } = "[]";
    public string Status { get; set; } = nameof(ContainerRegistrationStatus.Registered);
    public string? RuntimeContainerId { get; set; }
    public int? MappedPort { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastDiscoveredAt { get; set; }
    public int MaxConcurrentInferences { get; set; } = 1;

    public ICollection<ContainerModelMappingEntity> ContainerModelMappings { get; set; } = [];
}

public sealed class ContainerModelMappingEntity
{
    public string Id { get; set; } = string.Empty;
    public string RegisteredRuntimeId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public DateTimeOffset DiscoveredAt { get; set; }

    public RegisteredRuntimeEntity RegisteredRuntime { get; set; } = null!;
    public ModelEntity Model { get; set; } = null!;
}

public sealed class PromptEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    /// <summary>
    /// Per-prompt benchmark generation cap used when this prompt drives a manual
    /// benchmark run. Defaults to <see cref="BenchmarkDefaults.MaxTokens"/>.
    /// </summary>
    public int MaxTokens { get; set; } = BenchmarkDefaults.MaxTokens;
    public int CurrentVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PromptVersionEntity
{
    public string Id { get; set; } = string.Empty;
    public string PromptId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public PromptEntity Prompt { get; set; } = null!;
}

/// <summary>
/// Managed API key. Only a cryptographic hash of the secret is persisted; the
/// plaintext is returned exactly once at creation and the KeyPrefix (first few
/// characters) is what is ever shown afterwards. Scope decides which protected
/// surface the key can authenticate (see <see cref="ApiKeyScope"/>).
/// </summary>
public sealed class ApiKeyEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public ApiKeyScope Scope { get; set; } = ApiKeyScope.Inference;
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// Agent name this key is permanently bound to, or null for an unbound key.
    /// A key created with a bound name can only ever authenticate as that agent;
    /// an unbound agent-scope key binds to the first agent_name claimed during
    /// the /ws/agent handshake and is then permanently fixed to it.
    /// </summary>
    public string? BoundAgentName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// Per-key model access restrictions as JSON: {"providers":[...],"models":[...]}.
    /// Both arrays empty (or "{}"-shaped defaults) mean unrestricted access.
    /// </summary>
    public string AccessJson { get; set; } = "{}";
}

/// <summary>
/// Cloud LLM provider registration. The API key is stored encrypted at rest
/// via ASP.NET DataProtection; the plaintext never leaves the forwarding
/// service's memory. <see cref="ApiKeyHint"/> is a masked preview captured
/// at create/update time (e.g. "sk-…3f9a") and is never derived from the
/// ciphertext — it is stored separately as a plain string column.
/// </summary>
public sealed class CloudProviderEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKeyCiphertext { get; set; } = string.Empty;
    public string ApiKeyHint { get; set; } = string.Empty;
    /// <summary>JSON array of model id strings (same pattern as ExtraLabelsJson).</summary>
    public string ModelsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UsageRecordEntity
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    /// <summary>
    /// UtcTicks mirror of <see cref="Timestamp"/> as a plain long so SQLite can
    /// translate range comparisons (same pattern as <see cref="LogEntity.TimestampTicks"/>).
    /// </summary>
    public long TimestampTicks { get; set; }
    public string Provider { get; set; } = string.Empty;  // "local" or cloud provider name
    /// <summary>
    /// Discriminator for <see cref="Provider"/>: "cloud" (named cloud subscription)
    /// or "local" (self-hosted registered runtime).
    /// </summary>
    public string ProviderKind { get; set; } = "local";
    public string Model { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int CachedTokens { get; set; }
    public bool IsStreaming { get; set; }
    public long ElapsedMs { get; set; }

    /// <summary>Managed API key id that made the request, when attributable.</summary>
    public string? ApiKeyId { get; set; }

    /// <summary>
    /// Snapshot of the key's display name at record time — kept denormalized so
    /// attribution survives key deletion.
    /// </summary>
    public string? ApiKeyName { get; set; }
}

public class UnswarmDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<ModelEntity> Models => Set<ModelEntity>();
    public DbSet<BenchmarkHistoryEntity> Benchmarks => Set<BenchmarkHistoryEntity>();
    public DbSet<LogEntity> Logs => Set<LogEntity>();
    public DbSet<SettingsEntity> Settings => Set<SettingsEntity>();
    public DbSet<RegisteredRuntimeEntity> RegisteredRuntimes => Set<RegisteredRuntimeEntity>();
    public DbSet<ContainerModelMappingEntity> ContainerModelMappings => Set<ContainerModelMappingEntity>();
    public DbSet<PromptEntity> Prompts => Set<PromptEntity>();
    public DbSet<PromptVersionEntity> PromptVersions => Set<PromptVersionEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<CloudProviderEntity> CloudProviders => Set<CloudProviderEntity>();
    public DbSet<UsageRecordEntity> UsageRecords => Set<UsageRecordEntity>();

    public UnswarmDbContext(DbContextOptions<UnswarmDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ModelEntity>(e =>
        {
            e.HasKey(m => m.Id);
            // Benchmark HISTORY: many rows per model. The LastBenchmark nav property
            // is kept for compatibility but is explicitly ignored — enforcing a 1:1
            // FK here would create a UNIQUE constraint on Benchmarks.ModelId, which
            // conflicts with the benchmark-history list (newest-first, max 50).
            e.Ignore(m => m.LastBenchmark);
            e.HasOne(m => m.SourceRuntime)
             .WithMany()
             .HasForeignKey(m => m.SourceRuntimeId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BenchmarkHistoryEntity>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasIndex(b => b.ModelId);
        });

        modelBuilder.Entity<LogEntity>(e =>
        {
            e.HasKey(l => l.Id);
            // All log queries (retention cutoff, newest-first history) filter and
            // order on the TimestampTicks mirror — the SQLite provider cannot
            // translate DateTimeOffset comparisons, so the plain Timestamp index
            // was queried by nothing and was dropped in its favor.
            e.HasIndex(l => l.TimestampTicks);
        });

        modelBuilder.Entity<SettingsEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.ParallelSlotSkipLimit).HasDefaultValue(3);
            e.HasData(new SettingsEntity
            {
                Id = "default",
                RequestTimeout = 120,
                HealthCheckInterval = 10,
                AutoShutdownIdle = false,
                IdleTimeout = 300,
                LogRetention = 168,
                EnableBenchmarking = true,
                PriorityMode = "fifo",
                BatchDrain = false,
                LazyStop = true,
                MaxQueueDepth = 32,
                MaxConcurrentTargets = 0,
                ParallelSlotSkipLimit = 3,
                EnableParallelSlotSkip = false,
                QueueStepsTillReset = 3,
                EnableConversationAffinity = false,
                ConversationDwellSeconds = 45,
                HideOriginPrefix = false,
                AgentDisplayNames = "{}",
                UsageRetentionDays = 30,
                ProviderBudgetsJson = "{}"
            });
        });

        modelBuilder.Entity<RegisteredRuntimeEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.MaxConcurrentInferences).HasDefaultValue(1);
        });

        modelBuilder.Entity<ContainerModelMappingEntity>(e =>
        {
            e.HasKey(cm => cm.Id);
            e.HasIndex(cm => new { cm.RegisteredRuntimeId, cm.ModelId }).IsUnique();
            e.HasOne(cm => cm.RegisteredRuntime)
             .WithMany(rc => rc.ContainerModelMappings)
             .HasForeignKey(cm => cm.RegisteredRuntimeId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(cm => cm.Model)
             .WithMany(m => m.ContainerModelMappings)
             .HasForeignKey(cm => cm.ModelId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromptEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired();
            e.Property(p => p.Text).IsRequired();
            // DB-level default backfills existing rows when the column is added.
            e.Property(p => p.MaxTokens).HasDefaultValue(BenchmarkDefaults.MaxTokens);
        });

        modelBuilder.Entity<PromptVersionEntity>(e =>
        {
            e.HasKey(pv => pv.Id);
            e.HasIndex(pv => new { pv.PromptId, pv.Version }).IsUnique();
            e.HasOne(pv => pv.Prompt)
             .WithMany()
             .HasForeignKey(pv => pv.PromptId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(pv => pv.Text).IsRequired();
        });

        modelBuilder.Entity<ApiKeyEntity>(e =>
        {
            e.HasKey(k => k.Id);
            e.Property(k => k.Name).IsRequired();
            e.Property(k => k.KeyHash).IsRequired();
            e.Property(k => k.KeyPrefix).IsRequired();
            e.HasIndex(k => k.Scope);
            e.HasIndex(k => k.IsActive);
            // DB-level default backfills existing rows when the column is added.
            e.Property(k => k.AccessJson).IsRequired().HasDefaultValue("{}").HasMaxLength(8192);
        });

        modelBuilder.Entity<CloudProviderEntity>(e =>
        {
            e.HasKey(cp => cp.Id);
            e.Property(cp => cp.Name).IsRequired().HasMaxLength(128);
            e.Property(cp => cp.BaseUrl).IsRequired().HasMaxLength(512);
            e.Property(cp => cp.ApiKeyCiphertext).IsRequired();
            e.Property(cp => cp.ApiKeyHint).IsRequired().HasMaxLength(64);
            e.Property(cp => cp.ModelsJson).IsRequired().HasMaxLength(65536);
            e.HasIndex(cp => cp.Name).IsUnique();
        });

        modelBuilder.Entity<UsageRecordEntity>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.TimestampTicks);
            e.HasIndex(u => new { u.Provider, u.Model });
            e.HasIndex(u => u.ApiKeyId);
            e.Property(u => u.Provider).IsRequired().HasMaxLength(128);
            e.Property(u => u.Model).IsRequired().HasMaxLength(256);
            e.Property(u => u.ApiKeyName).HasMaxLength(256);
            // DB-level default backfills existing rows when the column is added;
            // the migration additionally flips Provider == 'cloud' rows to 'cloud'.
            e.Property(u => u.ProviderKind).IsRequired().HasDefaultValue("local").HasMaxLength(16);
        });
    }
}
