using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Validation;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ModelRegistryTests
{
    private static ILogger<ModelRegistry> Log() => new LoggerFactory().CreateLogger<ModelRegistry>();

    private static ModelValidator StubValidator()
    {
        var logger = new LoggerFactory().CreateLogger<ModelValidator>();
        var opts = Options.Create(new ContainerHostOptions { Host = "127.0.0.1" });
        return new ModelValidator(logger, opts);
    }

    private static (Func<UnswarmDbContext> factory, SqliteConnection connection) BuildDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = () =>
        {
            var options = new DbContextOptionsBuilder<UnswarmDbContext>()
                .UseSqlite(connection)
                .Options;
            return new UnswarmDbContext(options);
        };
        using (var db = factory())
        {
            db.Database.EnsureCreated();
        }
        return (factory, connection);
    }

    private static ModelDefinition MakeDef(
        string id = "model-1",
        string name = "llama-3.1-8b",
        string family = "llama",
        string paramSize = "8B",
        string quantization = "Q4_K_M",
        ModelStatus status = ModelStatus.Validating,
        int contextWindow = 8192,
        string containerImage = "llama:latest",
        string? displayName = null) => new()
    {
        Id = id,
        Name = name,
        Family = family,
        ParameterSize = paramSize,
        Quantization = quantization,
        Status = status,
        ContextWindow = contextWindow,
        ContainerImage = containerImage,
        DisplayName = displayName,
    };

    // ── ListAllAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListAllAsync_ReturnsAllModelsOrderedByName()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            await registry.CreateAsync(MakeDef(id: "m-2", name: "z-model"));
            await registry.CreateAsync(MakeDef(id: "m-1", name: "a-model"));
            await registry.CreateAsync(MakeDef(id: "m-3", name: "m-model"));

            var list = await registry.ListAllAsync();
            Assert.Equal(3, list.Count);
            Assert.Equal("a-model", list[0].Name);
            Assert.Equal("m-model", list[1].Name);
            Assert.Equal("z-model", list[2].Name);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task ListAllAsync_DetectsDisplayNameConflicts_SetsConflict()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            await registry.CreateAsync(MakeDef(id: "m-1", name: "llama-8b-q4", displayName: "Llama 3.1 8B"));
            await registry.CreateAsync(MakeDef(id: "m-2", name: "llama-8b-q8", displayName: "Llama 3.1 8B"));

            var list = await registry.ListAllAsync();
            Assert.All(list, m => Assert.Equal(ModelStatus.Conflict, m.Status));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task ListAllAsync_ResolvesSingleConflict_BackToReady()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            // Create two with same display name
            await registry.CreateAsync(MakeDef(id: "m-1", name: "llama-8b-q4", displayName: "Llama 3.1 8B"));
            await registry.CreateAsync(MakeDef(id: "m-2", name: "llama-8b-q8", displayName: "Llama 3.1 8B"));

            // First ListAllAsync detects conflict — both get Conflict status
            var firstPass = await registry.ListAllAsync();
            Assert.All(firstPass, m => Assert.Equal(ModelStatus.Conflict, m.Status));

            // Delete one — the remaining one should resolve back to Ready on next ListAllAsync
            await registry.DeleteAsync("m-2");

            var list = await registry.ListAllAsync();
            Assert.Single(list);
            Assert.Equal(ModelStatus.Ready, list[0].Status);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task ListAllAsync_EmptyDatabase_ReturnsEmptyList()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var list = await registry.ListAllAsync();
            Assert.Empty(list);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task ListAllAsync_NoConflictWhenDisplayNameNull_UsesInternalName()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            await registry.CreateAsync(MakeDef(id: "m-1", name: "llama-8b-q4"));
            await registry.CreateAsync(MakeDef(id: "m-2", name: "llama-8b-q8"));

            var list = await registry.ListAllAsync();
            // Different internal names, no conflict — statuses stay as created (Validating)
            Assert.All(list, m => Assert.Equal(ModelStatus.Validating, m.Status));
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── GetAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReturnsModelById()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var def = MakeDef(id: "m-1", name: "mistral-7b");
            await registry.CreateAsync(def);

            var result = await registry.GetAsync("m-1");
            Assert.NotNull(result);
            Assert.Equal("mistral-7b", result.Name);
            Assert.Equal("llama", result.Family);
            Assert.Equal("8B", result.ParameterSize);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetAsync_Missing_ReturnsNull()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var result = await registry.GetAsync("nonexistent");
            Assert.Null(result);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── GetByNameAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetByNameAsync_FindsByInternalName()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            await registry.CreateAsync(MakeDef(id: "m-1", name: "mistral-7b"));

            var result = await registry.GetByNameAsync("mistral-7b");
            Assert.NotNull(result);
            Assert.Equal("m-1", result.Id);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetByNameAsync_Missing_ReturnsNull()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var result = await registry.GetByNameAsync("nonexistent");
            Assert.Null(result);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── CreateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_HappyPath_StoresWithTimestamps()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var def = MakeDef(id: "m-1", name: "mistral-7b");

            var result = await registry.CreateAsync(def);

            Assert.Equal("m-1", result.Id);
            Assert.Equal("mistral-7b", result.Name);
            Assert.Equal(clock.UtcNow, result.CreatedAt);
            Assert.Equal(clock.UtcNow, result.UpdatedAt);

            // Verify it's persisted
            var fetched = await registry.GetAsync("m-1");
            Assert.NotNull(fetched);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task CreateAsync_CloudPrefixInName_ThrowsInvalidOperationException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var def = MakeDef(id: "m-1", name: "cloud/gpt-4o");

            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateAsync(def));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task CreateAsync_CloudPrefixInId_ThrowsInvalidOperationException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var def = MakeDef(id: "cloud/model-1", name: "gpt-4o");

            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateAsync(def));
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── UpdateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_UpdatesAllFields()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            await registry.CreateAsync(MakeDef(id: "m-1", name: "mistral-7b"));

            clock.Advance(TimeSpan.FromMinutes(1));
            var updated = MakeDef(
                id: "m-1",
                name: "mistral-7b-instruct",
                family: "mistral",
                paramSize: "7B",
                quantization: "Q8_0",
                status: ModelStatus.Ready,
                contextWindow: 32768,
                containerImage: "mistral-instruct:latest",
                displayName: "Mistral 7B Instruct");

            var result = await registry.UpdateAsync("m-1", updated);

            Assert.Equal("mistral-7b-instruct", result.Name);
            Assert.Equal("mistral", result.Family);
            Assert.Equal("7B", result.ParameterSize);
            Assert.Equal("Q8_0", result.Quantization);
            Assert.Equal(ModelStatus.Ready, result.Status);
            Assert.Equal(32768, result.ContextWindow);
            Assert.Equal("mistral-instruct:latest", result.ContainerImage);
            Assert.Equal("Mistral 7B Instruct", result.DisplayName);
            Assert.Equal(clock.UtcNow, result.UpdatedAt);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateAsync_Missing_ThrowsInvalidOperationException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.UpdateAsync("nonexistent", MakeDef()));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateAsync_CloudPrefixInName_ThrowsInvalidOperationException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            await registry.CreateAsync(MakeDef(id: "m-1", name: "llama-8b"));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.UpdateAsync("m-1", MakeDef(id: "m-1", name: "cloud/some-model")));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateAsync_CloudPrefixInId_ThrowsInvalidOperationException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            await registry.CreateAsync(MakeDef(id: "m-1", name: "llama-8b"));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.UpdateAsync("m-1", MakeDef(id: "cloud/model-1", name: "llama-8b")));
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── DeleteAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesEntity()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            await registry.CreateAsync(MakeDef(id: "m-1"));

            await registry.DeleteAsync("m-1");

            var result = await registry.GetAsync("m-1");
            Assert.Null(result);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task DeleteAsync_Missing_IsNoOp_NoThrow()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            // Should not throw
            await registry.DeleteAsync("nonexistent");
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── ValidateAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_SetsStatusToReady()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            await registry.CreateAsync(MakeDef(id: "m-1", status: ModelStatus.Validating));

            await registry.ValidateAsync("m-1");

            var result = await registry.GetAsync("m-1");
            Assert.NotNull(result);
            Assert.Equal(ModelStatus.Ready, result.Status);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task ValidateAsync_MissingModel_IsNoOp()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            // Should not throw
            await registry.ValidateAsync("nonexistent");
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── Timestamps ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_UsesClockUtcNow()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            clock.UtcNow = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            var result = await registry.CreateAsync(MakeDef(id: "m-1"));

            Assert.Equal(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero), result.CreatedAt);
            Assert.Equal(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero), result.UpdatedAt);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateAsync_UpdatesTimestampFromClock()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            clock.UtcNow = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            await registry.CreateAsync(MakeDef(id: "m-1"));

            clock.UtcNow = new DateTimeOffset(2025, 6, 15, 13, 0, 0, TimeSpan.Zero);
            var result = await registry.UpdateAsync("m-1", MakeDef(id: "m-1", name: "updated-name"));

            Assert.Equal(new DateTimeOffset(2025, 6, 15, 13, 0, 0, TimeSpan.Zero), result.UpdatedAt);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── Multiple models with display name edge cases ───────────────────────

    [Fact]
    public async Task ListAllAsync_ThreeSameDisplayName_AllConflict()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            await registry.CreateAsync(MakeDef(id: "m-1", name: "model-a", displayName: "My Model"));
            await registry.CreateAsync(MakeDef(id: "m-2", name: "model-b", displayName: "My Model"));
            await registry.CreateAsync(MakeDef(id: "m-3", name: "model-c", displayName: "My Model"));

            var list = await registry.ListAllAsync();
            Assert.Equal(3, list.Count);
            Assert.All(list, m => Assert.Equal(ModelStatus.Conflict, m.Status));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task ListAllAsync_DeleteTwoOfThreeSameDisplayName_RemainingResolvesToReady()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());

            await registry.CreateAsync(MakeDef(id: "m-1", name: "model-a", displayName: "My Model"));
            await registry.CreateAsync(MakeDef(id: "m-2", name: "model-b", displayName: "My Model"));
            await registry.CreateAsync(MakeDef(id: "m-3", name: "model-c", displayName: "My Model"));

            // First pass triggers conflict detection
            await registry.ListAllAsync();

            await registry.DeleteAsync("m-1");
            await registry.DeleteAsync("m-2");

            var list = await registry.ListAllAsync();
            Assert.Single(list);
            Assert.Equal(ModelStatus.Ready, list[0].Status);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── SupportedThinkingEffortsJson ───────────────────────────────────────

    [Fact]
    public async Task CreateAsync_PreservesSupportedThinkingEffortsJson()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var clock = new FakeClock();
            var registry = new ModelRegistry(factory, clock, StubValidator(), Log());
            var def = MakeDef(id: "m-1");
            var defWithThinking = new ModelDefinition
            {
                Id = "m-1",
                Name = def.Name,
                Family = def.Family,
                ParameterSize = def.ParameterSize,
                Quantization = def.Quantization,
                Status = def.Status,
                ContextWindow = def.ContextWindow,
                ContainerImage = def.ContainerImage,
                SupportedThinkingEffortsJson = "[\"low\",\"medium\",\"high\"]",
            };

            var result = await registry.CreateAsync(defWithThinking);

            Assert.Equal("[\"low\",\"medium\",\"high\"]", result.SupportedThinkingEffortsJson);

            var fetched = await registry.GetAsync("m-1");
            Assert.NotNull(fetched);
            Assert.Equal("[\"low\",\"medium\",\"high\"]", fetched.SupportedThinkingEffortsJson);
        }
        finally { await conn.DisposeAsync(); }
    }
}
