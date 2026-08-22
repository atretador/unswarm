using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Unswarm.Core.Persistence;

/// <summary>
/// Applies SQLite connection-level tuning on EVERY connection open:
/// <list type="bullet">
/// <item><c>PRAGMA journal_mode=WAL</c> — write-ahead logging lets readers and a
/// writer proceed concurrently instead of serializing on the database lock.</item>
/// <item><c>PRAGMA busy_timeout=5000</c> — retry locked writes for up to 5s
/// instead of failing immediately with SQLITE_BUSY under concurrency.</item>
/// </list>
/// Both are idempotent; WAL is persistent per database file but re-issuing it on
/// each open is cheap and keeps behavior uniform across fresh and existing DBs.
/// Registered via <c>AddInterceptors</c> on every <see cref="UnswarmDbContext"/>
/// options configuration (DI registration and manual factory alike).
/// </summary>
public sealed class SqliteTuningInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteTuningInterceptor Instance = new();

    private SqliteTuningInterceptor() { }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyPragmas(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        return Task.CompletedTask;
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        // journal_mode=WAL returns the resulting mode as a scalar row.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteScalar();
    }
}
