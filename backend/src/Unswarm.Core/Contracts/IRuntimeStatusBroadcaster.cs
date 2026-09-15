using System.Threading.Channels;

namespace Unswarm.Core.Contracts;

/// <summary>
/// A runtime status change event, broadcast to SSE subscribers
/// when agent telemetry reports container/script status changes.
/// </summary>
public sealed record RuntimeStatusEvent(
    string AgentName,
    DateTimeOffset Timestamp,
    IReadOnlyList<ContainerStatusUpdate> Containers,
    IReadOnlyList<ScriptStatusUpdate> Scripts);

public sealed record ContainerStatusUpdate(
    string Id,
    string? Name,
    string Status,
    int? Port);

public sealed record ScriptStatusUpdate(
    string? Path,
    string Status,
    string? RegistrationId,
    int Port);

/// <summary>
/// In-process fan-out of runtime status changes to SSE subscribers.
/// </summary>
public interface IRuntimeStatusBroadcaster
{
    IRuntimeStatusSubscription Subscribe();
    void Publish(RuntimeStatusEvent evt);
}

public interface IRuntimeStatusSubscription : IDisposable
{
    ChannelReader<RuntimeStatusEvent> Reader { get; }
}
