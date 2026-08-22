using System.Buffers;
using System.Text;
using System.Text.Json;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

/// <summary>
/// Stream decorator that taps an SSE body stream, incrementally counting tokens
/// without buffering the entire response. On EOF or dispose it writes final
/// <see cref="InferenceResponse.TokensGenerated"/> and
/// <see cref="InferenceResponse.ServerTokensPerSec"/> back onto the response.
///
/// Byte-boundary-safe: carries a partial-line buffer across reads and caps line
/// length at ~1 MB (longer lines stop being scanned to avoid OOM).
/// </summary>
internal sealed class StreamingTokenTapStream : Stream
{
    private const int MaxLineLength = 1_048_576; // 1 MB

    private readonly Stream _inner;
    private readonly InferenceResponse _response;
    private readonly StringBuilder _lineBuffer = new();
    private readonly byte[] _crLf = Encoding.UTF8.GetBytes("\r\n");
    private int _deltaCount;
    private int _usageTokens;
    private int _cachedTokens;
    private double _serverTps;
    private double _promptTps;
    private bool _disposed;

    public StreamingTokenTapStream(Stream inner, InferenceResponse response)
    {
        _inner = inner;
        _response = response;
    }

    // ── Stream pass-through ────────────────────────────────────────────────
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0) TapBytes(buffer, offset, n);
        else Finalize();
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
        if (n > 0) TapBytes(buffer, offset, n);
        else Finalize();
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (n > 0)
        {
            // Use Span for the tap — avoids allocation
            var span = buffer.Span[..n];
            TapSpan(span);
        }
        else Finalize();
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // ── Dispose: finalize token counts ────────────────────────────────────
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            Finalize();
            _inner.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Finalize();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    // ── SSE scanning ──────────────────────────────────────────────────────

    private void TapBytes(byte[] buffer, int offset, int count)
    {
        TapSpan(buffer.AsSpan(offset, count));
    }

    private void TapSpan(ReadOnlySpan<byte> data)
    {
        // Split on \n (handles both \n and \r\n line endings)
        int searchStart = 0;
        while (searchStart < data.Length)
        {
            var nlIndex = data[searchStart..].IndexOf((byte)'\n');
            if (nlIndex < 0)
            {
                // No newline found — buffer the remainder
                if (_lineBuffer.Length + (data.Length - searchStart) <= MaxLineLength)
                    _lineBuffer.Append(Encoding.UTF8.GetString(data[searchStart..]));
                return;
            }

            // Found a newline — append the segment up to (but not including) the \n
            var segment = data[searchStart..(searchStart + nlIndex)];
            if (_lineBuffer.Length + segment.Length <= MaxLineLength)
                _lineBuffer.Append(Encoding.UTF8.GetString(segment));

            // Process the complete line
            ProcessLine(_lineBuffer.ToString().TrimEnd('\r'));
            _lineBuffer.Clear();

            searchStart += nlIndex + 1;
        }
    }

    private void ProcessLine(string line)
    {
        // SSE lines look like: "data: {json}" or "data: [DONE]"
        if (!line.StartsWith("data:", StringComparison.Ordinal))
            return;

        var json = line["data:".Length..].TrimStart();
        if (json.Length == 0 || json == "[DONE]")
            return;

        ScanJsonChunk(json);
    }

    private void ScanJsonChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 1. Check for usage.completion_tokens (final usage chunk)
            if (root.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("completion_tokens", out var ct)
                && ct.ValueKind == JsonValueKind.Number
                && ct.TryGetInt32(out var n))
            {
                _usageTokens = n;
            }

            // 1b. Check for usage.prompt_tokens_details.cached_tokens (vLLM / llama.cpp OpenAI-compat)
            if (root.TryGetProperty("usage", out var usageForCache)
                && usageForCache.TryGetProperty("prompt_tokens_details", out var details)
                && details.TryGetProperty("cached_tokens", out var cached)
                && cached.ValueKind == JsonValueKind.Number
                && cached.TryGetInt32(out var cn))
            {
                _cachedTokens = cn;
            }

            // 1c. Check for tokens_cached (llama.cpp native endpoint)
            if (root.TryGetProperty("tokens_cached", out var tc)
                && tc.ValueKind == JsonValueKind.Number
                && tc.TryGetInt32(out var tcn))
            {
                _cachedTokens = tcn;
            }

            // 2. Check for choices[].delta.content → count as ~1 token each
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        Interlocked.Increment(ref _deltaCount);
                    }
                }
            }

            // 3. Check for timings.predicted_per_second (llama.cpp server)
            if (root.TryGetProperty("timings", out var timings)
                && timings.TryGetProperty("predicted_per_second", out var pps)
                && pps.ValueKind == JsonValueKind.Number
                && pps.TryGetDouble(out var rate)
                && rate > 0)
            {
                _serverTps = rate;
            }

            // 4. Check for timings.prompt_per_second (llama.cpp prompt processing speed)
            if (root.TryGetProperty("timings", out var timings2)
                && timings2.TryGetProperty("prompt_per_second", out var pps2)
                && pps2.ValueKind == JsonValueKind.Number
                && pps2.TryGetDouble(out var promptRate)
                && promptRate > 0)
            {
                _promptTps = promptRate;
            }
        }
        catch
        {
            // best-effort; malformed JSON lines are silently ignored
        }
    }

    private void Finalize()
    {
        // Flush any remaining partial line
        if (_lineBuffer.Length > 0)
        {
            ProcessLine(_lineBuffer.ToString().TrimEnd('\r'));
            _lineBuffer.Clear();
        }

        // Prefer explicit usage count; fall back to delta counting
        _response.TokensGenerated = _usageTokens > 0 ? _usageTokens : _deltaCount;
        _response.ServerTokensPerSec = _serverTps;
        _response.ServerPromptTokensPerSec = _promptTps;
        _response.PromptTokensCached = _cachedTokens;
    }
}
