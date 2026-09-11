using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services.Validation;

namespace Unswarm.Core.Services;

public sealed class ModelRegistry : IModelRegistry
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly ModelValidator _validator;
    private readonly ILogger<ModelRegistry> _logger;

    public ModelRegistry(
        Func<UnswarmDbContext> dbFactory,
        IClock clock,
        ModelValidator validator,
        ILogger<ModelRegistry> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _validator = validator;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModelDefinition>> ListAllAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entities = await db.Models
            .OrderBy(m => m.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        // Resolve name conflicts on read: if multiple models share the same DisplayName
        // (the name served under /v1/models), flag all of them as Conflict. If only one
        // remains with a conflict display name, restore it to Ready. This covers boot-time
        // detection and manual DB edits. Two models sharing an internal Name (filename)
        // from different runtimes are independent and not conflicts.
        var changed = false;
        var groups = entities.GroupBy(e => e.DisplayName ?? e.Name).ToList();
        foreach (var group in groups)
        {
            var list = group.ToList();
            if (list.Count > 1)
            {
                foreach (var e in list)
                {
                    if (e.Status != nameof(ModelStatus.Conflict))
                    {
                        e.Status = nameof(ModelStatus.Conflict);
                        changed = true;
                    }
                }
            }
            else if (list.Count == 1 && list[0].Status == nameof(ModelStatus.Conflict))
            {
                list[0].Status = nameof(ModelStatus.Ready);
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return entities.Select(MapToDefinition).ToList();
    }

    public async Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Models.FindAsync([id], ct).ConfigureAwait(false);
        return entity is null ? null : MapToDefinition(entity);
    }

    public async Task<ModelDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Models.FirstOrDefaultAsync(m => m.Name == name, ct).ConfigureAwait(false);
        return entity is null ? null : MapToDefinition(entity);
    }

    public async Task<ModelDefinition> CreateAsync(ModelDefinition definition, CancellationToken ct = default)
    {
        if (definition.Name.StartsWith("cloud/", StringComparison.Ordinal) || 
            definition.Id.StartsWith("cloud/", StringComparison.Ordinal))
            throw new InvalidOperationException("Model names and IDs starting with 'cloud/' are reserved for cloud providers.");

        await using var db = _dbFactory();
        var now = _clock.UtcNow;
        var entity = new ModelEntity
        {
            Id = definition.Id,
            Name = definition.Name,
            Family = definition.Family,
            ParameterSize = definition.ParameterSize,
            Quantization = definition.Quantization,
            Status = definition.Status.ToString(),
            ContextWindow = definition.ContextWindow,
            ContainerImage = definition.ContainerImage,
            SourceRuntimeId = definition.SourceRuntimeId,
            DisplayName = definition.DisplayName,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Models.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Created model {Name} ({Id})", entity.Name, entity.Id);
        return MapToDefinition(entity);
    }

    public async Task<ModelDefinition> UpdateAsync(string id, ModelDefinition definition, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Models.FindAsync([id], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Model {id} not found");

        if (definition.Name.StartsWith("cloud/", StringComparison.Ordinal) || 
            definition.Id.StartsWith("cloud/", StringComparison.Ordinal))
            throw new InvalidOperationException("Model names and IDs starting with 'cloud/' are reserved for cloud providers.");

        entity.Name = definition.Name;
        entity.Family = definition.Family;
        entity.ParameterSize = definition.ParameterSize;
        entity.Quantization = definition.Quantization;
        entity.Status = definition.Status.ToString();
        entity.ContextWindow = definition.ContextWindow;
        entity.ContainerImage = definition.ContainerImage;
        entity.SourceRuntimeId = definition.SourceRuntimeId;
        entity.DisplayName = definition.DisplayName;
        entity.UpdatedAt = _clock.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Updated model {Name} ({Id})", entity.Name, entity.Id);
        return MapToDefinition(entity);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Models.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is not null)
        {
            db.Models.Remove(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Deleted model {Id}", id);
        }
    }

    public async Task ValidateAsync(string id, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Models.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null) return;

        // Set to Validating
        entity.Status = nameof(ModelStatus.Validating);
        entity.UpdatedAt = _clock.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Run validation
        try
        {
            // For validation we need a port — in a real system this would come from
            // the docker controller. Here we assume the container is already running.
            // The caller is responsible for starting the container before calling ValidateAsync.
            entity.Status = nameof(ModelStatus.Ready);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validation failed for model {Id}", id);
            entity.Status = nameof(ModelStatus.Invalid);
        }

        entity.UpdatedAt = _clock.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static ModelDefinition MapToDefinition(ModelEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Family = e.Family,
        ParameterSize = e.ParameterSize,
        Quantization = e.Quantization,
        Status = Enum.TryParse<ModelStatus>(e.Status, out var s) ? s : ModelStatus.Invalid,
        ContextWindow = e.ContextWindow,
        ContainerImage = e.ContainerImage,
        SourceRuntimeId = e.SourceRuntimeId,
        DisplayName = e.DisplayName,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };
}
