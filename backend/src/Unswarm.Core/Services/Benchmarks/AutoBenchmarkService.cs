using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services.Benchmarks;

/// <summary>
/// Shared benchmark constants and wire-format helpers used by the manual
/// flow (BenchmarksController). Keeping them in one place guarantees the
/// built-in prompt and the chat-completion payload stay byte-identical across flows.
/// </summary>
public static class BenchmarkDefaults
{
    public const int MaxTokens = 256;

    /// <summary>Floor for per-prompt generation caps. No ceiling — the cap is the user's call.</summary>
    public const int MinPromptMaxTokens = 1;

    /// <summary>
    /// Normalizes an optional per-prompt max-token cap: null → default cap,
    /// otherwise floored at <see cref="MinPromptMaxTokens"/> (no upper bound).
    /// </summary>
    public static int NormalizeMaxTokens(int? maxTokens) => maxTokens is null
        ? MaxTokens
        : Math.Max(MinPromptMaxTokens, maxTokens.Value);

    /// <summary>
    /// A LONGER, realistic instruction prompt so benchmarks measure real generation
    /// work, not a one-word smoke reply.
    /// </summary>
    public const string DefaultBenchmarkPrompt =
        "Write a detailed summary of the following text, covering the main arguments, " +
        "key supporting evidence, and any notable caveats. Keep the summary between " +
        "150 and 250 words, use clear paragraph structure, and end with a one-sentence " +
        "conclusion that states the overall significance of the text.\n\n" +
        "The rapid adoption of large language models has transformed how software is built, " +
        "from code generation to documentation. However, their deployment introduces new " +
        "operational concerns, including latency, cost, and the need for careful evaluation " +
        "against domain-specific benchmarks. Teams must balance model capability with " +
        "practical infrastructure constraints such as GPU availability, memory footprint, " +
        "and request concurrency. As models become more capable, the line between " +
        "assistive tooling and autonomous agents blurs, raising questions about oversight " +
        "and accountability in automated pipelines.";

    public static string BuildChatPayload(string modelId, string prompt)
        => BuildChatPayload(modelId, prompt, MaxTokens);

    public static string BuildChatPayload(string modelId, string prompt, int maxTokens)
    {
        var payload = new
        {
            model = modelId,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            max_tokens = maxTokens
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Upper bound on persisted response text (chars).</summary>
    public const int MaxStoredResponseChars = 8192;

    /// <summary>
    /// Both captured texts from a non-streaming completion body. Either may be
    /// null: thinking models (e.g. Qwen3.x on llama.cpp) put ALL generated text
    /// in <c>message.reasoning_content</c> with <c>content</c> == "" until
    /// reasoning finishes, so both fields must be captured independently.
    /// </summary>
    public sealed record ResponseParts(string? Content, string? Reasoning);

    /// <summary>
    /// Drains a non-streaming OpenAI-compatible completion body and extracts BOTH
    /// choices[0].message.content and choices[0].message.reasoning_content for
    /// persistence in benchmark history.
    /// Never throws: any read/parse/shape failure yields (null, null). Each field
    /// is truncated to <see cref="MaxStoredResponseChars"/> characters. The stream
    /// itself is not disposed here — ownership stays with the response object
    /// (tap/tunnel streams manage their own lifetime).
    /// </summary>
    public static async Task<ResponseParts> ExtractResponsePartsAsync(Stream? body, CancellationToken ct = default)
    {
        if (body is null) return new ResponseParts(null, null);
        try
        {
            using var buffered = new MemoryStream();
            await body.CopyToAsync(buffered, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffered.ToArray()));
            var message = doc.RootElement
                .GetProperty("choices")
                .EnumerateArray()
                .FirstOrDefault()
                .GetProperty("message");

            static string? ReadTruncated(JsonElement element, string propertyName)
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty(propertyName, out var value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                var text = value.GetString();
                if (string.IsNullOrEmpty(text)) return null;
                return text.Length <= MaxStoredResponseChars
                    ? text
                    : text[..MaxStoredResponseChars];
            }

            return new ResponseParts(
                Content: ReadTruncated(message, "content"),
                Reasoning: ReadTruncated(message, "reasoning_content"));
        }
        catch
        {
            return new ResponseParts(null, null);
        }
    }

    /// <summary>
    /// Legacy single-value extraction; delegates to <see cref="ExtractResponsePartsAsync"/>
    /// and returns only the content part. New call sites should use the parts method so
    /// reasoning text from thinking models is not lost.
    /// </summary>
    public static async Task<string?> ExtractResponseContentAsync(Stream? body, CancellationToken ct = default)
        => (await ExtractResponsePartsAsync(body, ct).ConfigureAwait(false)).Content;
}


