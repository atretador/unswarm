namespace Unswarm.Core.Contracts;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
