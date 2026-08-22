using System.Text;
using Unswarm.Core.Services.Benchmarks;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Extraction of BOTH content and reasoning_content from non-streaming
/// chat-completion bodies. Thinking models (e.g. Qwen3.x on llama.cpp) return
/// all generated text in message.reasoning_content with content == "" until
/// reasoning finishes, so both parts must be captured independently.
/// </summary>
public sealed class BenchmarkResponseExtractionTests
{
    private static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task ContentOnly_ExtractsContent_ReasoningNull()
    {
        var body = Body("""
            {"choices":[{"finish_reason":"stop","index":0,"message":{"role":"assistant","content":"Hello world"}}]}
            """);

        var parts = await BenchmarkDefaults.ExtractResponsePartsAsync(body);

        Assert.Equal("Hello world", parts.Content);
        Assert.Null(parts.Reasoning);
    }

    [Fact]
    public async Task ReasoningOnly_ContentEmpty_ContentNull_ReasoningCaptured()
    {
        // Verified llama.cpp wire shape for thinking models mid/post-reasoning.
        var body = Body("""
            {"choices":[{"finish_reason":"length","index":0,"message":{"role":"assistant","content":"","reasoning_content":"Let me think about this step by step..."}}],"usage":{"completion_tokens":42}}
            """);

        var parts = await BenchmarkDefaults.ExtractResponsePartsAsync(body);

        Assert.Null(parts.Content);
        Assert.Equal("Let me think about this step by step...", parts.Reasoning);
    }

    [Fact]
    public async Task BothPresent_BothCaptured()
    {
        var body = Body("""
            {"choices":[{"index":0,"message":{"role":"assistant","content":"Final answer","reasoning_content":"Thinking hard"}}]}
            """);

        var parts = await BenchmarkDefaults.ExtractResponsePartsAsync(body);

        Assert.Equal("Final answer", parts.Content);
        Assert.Equal("Thinking hard", parts.Reasoning);
    }

    [Fact]
    public async Task MalformedJson_BothNull_NeverThrows()
    {
        var parts = await BenchmarkDefaults.ExtractResponsePartsAsync(Body("{not json"));

        Assert.Null(parts.Content);
        Assert.Null(parts.Reasoning);
    }

    [Fact]
    public async Task NullStream_BothNull()
    {
        var parts = await BenchmarkDefaults.ExtractResponsePartsAsync(null);

        Assert.Null(parts.Content);
        Assert.Null(parts.Reasoning);
    }

    [Fact]
    public async Task MissingMessageOrWrongShape_BothNull_NeverThrows()
    {
        var noChoices = await BenchmarkDefaults.ExtractResponsePartsAsync(Body("""{"choices":[]}"""));
        Assert.Null(noChoices.Content);
        Assert.Null(noChoices.Reasoning);

        var nonStringFields = await BenchmarkDefaults.ExtractResponsePartsAsync(
            Body("""{"choices":[{"message":{"content":123}}]}"""));
        Assert.Null(nonStringFields.Content);
        Assert.Null(nonStringFields.Reasoning);
    }

    [Fact]
    public async Task LongFields_TruncatedPerField_ToMaxStoredResponseChars()
    {
        var longText = new string('x', BenchmarkDefaults.MaxStoredResponseChars + 100);
        var json = """{"choices":[{"message":{"content":"__C__","reasoning_content":"__R__"}}]}"""
            .Replace("__C__", longText)
            .Replace("__R__", longText);

        var parts = await BenchmarkDefaults.ExtractResponsePartsAsync(Body(json));

        Assert.Equal(BenchmarkDefaults.MaxStoredResponseChars, parts.Content!.Length);
        Assert.Equal(BenchmarkDefaults.MaxStoredResponseChars, parts.Reasoning!.Length);
    }

    [Fact]
    public async Task LegacySingleValueMethod_ReturnsContentPartOnly()
    {
        var body = Body("""
            {"choices":[{"message":{"content":"answer","reasoning_content":"thoughts"}}]}
            """);

        var content = await BenchmarkDefaults.ExtractResponseContentAsync(body);

        Assert.Equal("answer", content);
    }
}
