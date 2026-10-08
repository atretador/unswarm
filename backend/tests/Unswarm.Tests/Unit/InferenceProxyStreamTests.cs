using System.Net.Http;
using System.Reflection;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for InferenceProxy's private HttpResponseMessageStream (the response-body
/// wrapper whose Drained task gates scheduler slot release). It is a private nested
/// type, so it is instantiated via reflection and exercised through the Stream API.
/// </summary>
public sealed class InferenceProxyStreamTests
{
    private static readonly Type StreamType = typeof(InferenceProxy)
        .GetNestedType("HttpResponseMessageStream", BindingFlags.NonPublic)!;

    private static (Stream Stream, Task Drained) Create(Stream inner)
    {
        var ctor = StreamType.GetConstructor([typeof(HttpResponseMessage), typeof(Stream)])!;
        var response = new HttpResponseMessage();
        var stream = (Stream)ctor.Invoke([response, inner]);
        var drained = (Task)StreamType.GetProperty("Drained")!.GetValue(stream)!;
        return (stream, drained);
    }

    [Fact]
    public void Type_IsFound()
    {
        Assert.NotNull(StreamType);
    }

    [Fact]
    public async Task ReadAsync_ToEof_CompletesDrained()
    {
        using var inner = new MemoryStream("abc"u8.ToArray());
        var (stream, drained) = Create(inner);

        var buffer = new byte[16];
        var first = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
        Assert.Equal(3, first);
        Assert.False(drained.IsCompleted);

        var second = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
        Assert.Equal(0, second);
        Assert.True(drained.IsCompletedSuccessfully);

        await stream.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CompletesDrained()
    {
        var inner = new MemoryStream("abc"u8.ToArray());
        var (stream, drained) = Create(inner);

        await stream.DisposeAsync();

        Assert.True(drained.IsCompletedSuccessfully);
        Assert.False(inner.CanRead); // inner disposed
    }

    [Fact]
    public async Task DisposeAsync_InnerThrows_FaultsDrained()
    {
        var (stream, drained) = Create(new ThrowingStream(throwOnAsyncDispose: true));

        await stream.DisposeAsync();

        Assert.True(drained.IsFaulted);
    }

    [Fact]
    public void Dispose_InnerThrows_FaultsDrained()
    {
        var (stream, drained) = Create(new ThrowingStream(throwOnAsyncDispose: false));

        ((IDisposable)stream).Dispose();

        Assert.True(drained.IsFaulted);
    }

    private sealed class ThrowingStream : Stream
    {
        private readonly bool _throwOnAsyncDispose;

        public ThrowingStream(bool throwOnAsyncDispose) => _throwOnAsyncDispose = throwOnAsyncDispose;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        protected override void Dispose(bool disposing)
        {
            if (!_throwOnAsyncDispose)
                throw new IOException("dispose failed");
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
            => _throwOnAsyncDispose
                ? ValueTask.FromException(new IOException("async dispose failed"))
                : ValueTask.CompletedTask;
    }
}
