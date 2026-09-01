using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

internal static class TestRouterProfileStore
{
    public static IRouterProfileStore Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var db = new UnswarmDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        var factory = () => new UnswarmDbContext(options);
        return new RouterProfileStore(factory);
    }
}
