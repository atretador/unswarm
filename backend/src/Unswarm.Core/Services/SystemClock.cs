using Unswarm.Core.Contracts;

namespace Unswarm.Core.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
