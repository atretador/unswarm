using System.Net.WebSockets;

namespace Unswarm.Tests.Fakes;

public sealed class FakeWebSocket : WebSocket
{
    private readonly List<string> _sent = [];
    private readonly Queue<(WebSocketMessageType Type, byte[] Data)> _receiveQueue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private WebSocketState _state = WebSocketState.Open;

    public IReadOnlyList<string> SentMessages => _sent;
    public int CloseCallCount { get; private set; }

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;

    public void EnqueueReceive(WebSocketMessageType type, byte[] data)
    {
        _receiveQueue.Enqueue((type, data));
        _signal.Release();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        // A real WebSocket blocks on ReceiveAsync until a frame arrives.
        // Block here too so the connection stays alive until a message (or
        // explicit close) is enqueued.
        while (_receiveQueue.Count == 0)
        {
            await _signal.WaitAsync(cancellationToken);
        }

        var (type, data) = _receiveQueue.Dequeue();
        var count = Math.Min(data.Length, buffer.Count);
        if (count > 0)
            Buffer.BlockCopy(data, 0, buffer.Array!, buffer.Offset, count);
        return new WebSocketReceiveResult(count, type, endOfMessage: true);
    }

    public override Task SendAsync(
        ArraySegment<byte> data,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        if (data.Array is not null && data.Count > 0)
        {
            var json = System.Text.Encoding.UTF8.GetString(data.Array, data.Offset, data.Count);
            _sent.Add(json);
        }
        return Task.CompletedTask;
    }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        CloseCallCount++;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
    }
}
