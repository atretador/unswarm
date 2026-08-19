using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public sealed class BenchmarkResponse
{
    public double TokensPerSec { get; set; }
    public double LatencyMs { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    public static BenchmarkResponse FromResult(BenchmarkResult r) => new()
    {
        TokensPerSec = r.TokensPerSec,
        LatencyMs = r.LatencyMs,
        Timestamp = r.Timestamp
    };
}
