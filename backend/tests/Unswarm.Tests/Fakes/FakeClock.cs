using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
