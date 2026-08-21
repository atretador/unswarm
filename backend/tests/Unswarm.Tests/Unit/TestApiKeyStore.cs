using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Builds a real <see cref="ApiKeyStore"/> backed by a single shared, open
/// in-memory SQLite connection. The store resolves a fresh
/// <see cref="UnswarmDbContext"/> per call (it disposes each one), so the
/// connection must be shared and kept open across contexts for the in-memory
/// database to persist between calls.
/// </summary>
internal static class TestApiKeyStore
{
    public static IApiKeyStore Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open(); // shared connection: EF Core will not close it on dispose

        var options = new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite(connection)
            .Options;

        // Ensure the schema once; the context disposal here leaves the shared
        // connection open for the store's own short-lived contexts.
        using (var db = new UnswarmDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        var factory = () => new UnswarmDbContext(options);
        return new ApiKeyStore(factory);
    }
}
