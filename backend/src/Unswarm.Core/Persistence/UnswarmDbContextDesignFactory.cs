using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Unswarm.Core.Persistence;

/// <summary>
/// Design-time factory for `dotnet ef migrations add`. Without this, the EF tools
/// would try to run the API's Program.cs (top-level statements) to resolve the
/// DbContext, which performs full startup work (DB repair, seeding, service init).
/// The connection string here is never used against a real database during
/// migration scaffolding — it only needs to be a valid SQLite path.
/// </summary>
public sealed class UnswarmDbContextDesignFactory : IDesignTimeDbContextFactory<UnswarmDbContext>
{
    public UnswarmDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), "unswarm-design-time.db")}")
            .Options;

        return new UnswarmDbContext(options);
    }
}
