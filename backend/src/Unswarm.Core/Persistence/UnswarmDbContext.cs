using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Models;

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
}

public sealed class LogEntity
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = nameof(LogLevel.Info);
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
}

public sealed class SettingsEntity
{
    public string Id { get; set; } = "default";
    public int MaxConcurrentModels { get; set; } = 1;
    public string? DefaultModel { get; set; }
    public int RequestTimeout { get; set; } = 120;
    public int HealthCheckInterval { get; set; } = 10;
    public bool AutoShutdownIdle { get; set; } = true;
    public int IdleTimeout { get; set; } = 300;
    public int LogRetention { get; set; } = 168;
    public bool EnableBenchmarking { get; set; } = true;
    public string PriorityMode { get; set; } = "fifo";
    public bool BatchDrain { get; set; }
    public bool LazyStop { get; set; } = true;
    public int MaxQueueDepth { get; set; } = 32;
    public int MaxConcurrentTargets { get; set; }
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
            e.HasIndex(l => l.Timestamp);
        });

        modelBuilder.Entity<SettingsEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasData(new SettingsEntity
            {
                Id = "default",
                MaxConcurrentModels = 1,
                RequestTimeout = 120,
                HealthCheckInterval = 10,
                AutoShutdownIdle = true,
                IdleTimeout = 300,
                LogRetention = 168,
                EnableBenchmarking = true,
                PriorityMode = "fifo",
                BatchDrain = false,
                LazyStop = true,
                MaxQueueDepth = 32,
                MaxConcurrentTargets = 0
            });
        });

        modelBuilder.Entity<RegisteredRuntimeEntity>(e =>
        {
            e.HasKey(r => r.Id);
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
    }
}
