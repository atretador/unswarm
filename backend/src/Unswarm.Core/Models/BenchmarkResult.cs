namespace Unswarm.Core.Models;

public sealed class BenchmarkResult
{
    public double TokensPerSec { get; init; }
    public double LatencyMs { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
