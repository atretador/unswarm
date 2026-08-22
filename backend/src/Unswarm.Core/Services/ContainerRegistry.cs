using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

public sealed class ContainerRegistry : IContainerRegistry
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly ILogger<ContainerRegistry> _logger;

    public ContainerRegistry(
        Func<UnswarmDbContext> dbFactory,
        IClock clock,
        ILogger<ContainerRegistry> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entities = await db.RegisteredRuntimes
            .OrderBy(r => r.DisplayName)
            .ToListAsync(ct).ConfigureAwait(false);
        return entities.Select(MapToModel).ToList();
    }

    public async Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.RegisteredRuntimes.FindAsync([id], ct).ConfigureAwait(false);
        return entity is null ? null : MapToModel(entity);
    }

    public async Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var now = _clock.UtcNow;
        var entity = new RegisteredRuntimeEntity
        {
            Id = container.Id,
            DisplayName = container.DisplayName,
            Image = container.Image,
            ContainerPort = container.ContainerPort,
            GpuDevices = container.GpuDevices,
            MemoryLimitMb = container.MemoryLimitMb,
            ExtraLabelsJson = JsonSerializer.Serialize(container.ExtraLabels),
            Agent = string.IsNullOrWhiteSpace(container.Agent) ? "host" : container.Agent,
            CanRunAlongWithJson = JsonSerializer.Serialize(container.CanRunAlongWith ?? []),
            RuntimeKind = container.RuntimeKind.ToString(),
            LauncherPath = container.LauncherPath,
            RuntimeProcessId = container.RuntimeProcessId,
            Status = nameof(ContainerRegistrationStatus.Registered),
            RuntimeContainerId = container.RuntimeContainerId,
            MappedPort = container.MappedPort,
            ErrorMessage = container.ErrorMessage,
            CreatedAt = now,
            UpdatedAt = now,
            LastDiscoveredAt = container.LastDiscoveredAt,
            MaxConcurrentInferences = container.MaxConcurrentInferences
        };
        db.RegisteredRuntimes.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Created registered container {DisplayName} ({Id})", entity.DisplayName, entity.Id);
        return MapToModel(entity);
    }

    public async Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.RegisteredRuntimes.FindAsync([id], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Registered container {id} not found");

        entity.DisplayName = container.DisplayName;
        entity.Image = container.Image;
        entity.ContainerPort = container.ContainerPort;
        entity.RuntimeKind = container.RuntimeKind.ToString();
        entity.LauncherPath = container.LauncherPath;
        entity.RuntimeProcessId = container.RuntimeProcessId;
        entity.GpuDevices = container.GpuDevices;
        entity.MemoryLimitMb = container.MemoryLimitMb;
        entity.ExtraLabelsJson = JsonSerializer.Serialize(container.ExtraLabels);
        entity.Agent = string.IsNullOrWhiteSpace(container.Agent) ? "host" : container.Agent;
        entity.CanRunAlongWithJson = JsonSerializer.Serialize(container.CanRunAlongWith ?? []);
        entity.Status = container.Status.ToString();
        entity.RuntimeContainerId = container.RuntimeContainerId;
        entity.MappedPort = container.MappedPort;
        entity.ErrorMessage = container.ErrorMessage;
        entity.LastDiscoveredAt = container.LastDiscoveredAt;
        entity.MaxConcurrentInferences = container.MaxConcurrentInferences;
        entity.UpdatedAt = _clock.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Updated registered container {DisplayName} ({Id})", entity.DisplayName, entity.Id);
        return MapToModel(entity);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.RegisteredRuntimes.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is not null)
        {
            db.RegisteredRuntimes.Remove(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Deleted registered container {Id}", id);
        }
    }

    public async Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
    {
        await using var db = _dbFactory();

        var exists = await db.ContainerModelMappings
            .AnyAsync(cm => cm.RegisteredRuntimeId == registeredContainerId && cm.ModelId == modelId, ct)
            .ConfigureAwait(false);
        if (exists) return;

        var mapping = new ContainerModelMappingEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RegisteredRuntimeId = registeredContainerId,
            ModelId = modelId,
            DiscoveredAt = _clock.UtcNow
        };
        db.ContainerModelMappings.Add(mapping);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Added model mapping: container {ContainerId} -> model {ModelId}", registeredContainerId, modelId);
    }

    public async Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var mapping = await db.ContainerModelMappings
            .FirstOrDefaultAsync(cm => cm.RegisteredRuntimeId == registeredContainerId && cm.ModelId == modelId, ct)
            .ConfigureAwait(false);
        if (mapping is not null)
        {
            db.ContainerModelMappings.Remove(mapping);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Removed model mapping: container {ContainerId} -> model {ModelId}", registeredContainerId, modelId);
        }
    }

    public async Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        return await db.ContainerModelMappings
            .Where(cm => cm.RegisteredRuntimeId == registeredContainerId)
            .Select(cm => cm.ModelId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        // First try via the mapping table (model id lookup)
        var mapping = await db.ContainerModelMappings
            .Include(cm => cm.Model)
            .FirstOrDefaultAsync(cm => cm.ModelId == modelName || cm.Model.Name == modelName, ct)
            .ConfigureAwait(false);
        if (mapping is not null)
            return mapping.RegisteredRuntimeId;

        // Fallback: check ModelEntity.SourceRuntimeId directly
        var model = await db.Models
            .FirstOrDefaultAsync(m => m.Name == modelName || m.Id == modelName, ct)
            .ConfigureAwait(false);
        return model?.SourceRuntimeId;
    }

    private static RegisteredRuntime MapToModel(RegisteredRuntimeEntity e) => new()
    {
        Id = e.Id,
        DisplayName = e.DisplayName,
        Image = e.Image,
        ContainerPort = e.ContainerPort,
        RuntimeKind = Enum.TryParse<RuntimeKind>(e.RuntimeKind, out var rk) ? rk : RuntimeKind.Container,
        LauncherPath = e.LauncherPath,
        RuntimeProcessId = e.RuntimeProcessId,
        GpuDevices = e.GpuDevices,
        MemoryLimitMb = e.MemoryLimitMb,
        ExtraLabels = JsonSerializer.Deserialize<Dictionary<string, string>>(e.ExtraLabelsJson) ?? [],
        Agent = string.IsNullOrWhiteSpace(e.Agent) ? "host" : e.Agent,
        CanRunAlongWith = JsonSerializer.Deserialize<List<string>>(e.CanRunAlongWithJson) ?? [],
        Status = Enum.TryParse<ContainerRegistrationStatus>(e.Status, out var s) ? s : ContainerRegistrationStatus.Error,
        RuntimeContainerId = e.RuntimeContainerId,
        MappedPort = e.MappedPort,
        ErrorMessage = e.ErrorMessage,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        LastDiscoveredAt = e.LastDiscoveredAt,
        MaxConcurrentInferences = e.MaxConcurrentInferences
    };
}
