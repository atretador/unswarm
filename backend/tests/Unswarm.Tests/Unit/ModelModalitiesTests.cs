using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for <see cref="ModelModalities"/> — canonical token handling, admin-input
/// validation, OpenRouter modality-string parsing, and graceful JSON parsing.
/// </summary>
public sealed class ModelModalitiesTests
{
    // ── FindUnknownToken ──────────────────────────────────────────────

    [Fact]
    public void FindUnknownToken_UnknownToken_ReturnsTrimmedToken()
    {
        Assert.Equal("hologram", ModelModalities.FindUnknownToken(["text", "hologram"]));
        Assert.Equal("hologram", ModelModalities.FindUnknownToken(["  hologram  "]));
    }

    [Fact]
    public void FindUnknownToken_UppercaseKnownTokens_ReturnsNull()
    {
        Assert.Null(ModelModalities.FindUnknownToken(["TEXT", "IMAGE", "Pdf"]));
    }

    [Fact]
    public void FindUnknownToken_KnownTokens_ReturnsNull()
    {
        Assert.Null(ModelModalities.FindUnknownToken(["text", "image", "video", "audio", "pdf"]));
    }

    [Fact]
    public void FindUnknownToken_EmptyString_ReturnsEmptySentinel()
    {
        Assert.Equal("(empty)", ModelModalities.FindUnknownToken(["text", ""]));
    }

    [Fact]
    public void FindUnknownToken_Whitespace_ReturnsEmptySentinel()
    {
        Assert.Equal("(empty)", ModelModalities.FindUnknownToken(["   "]));
        Assert.Equal("(empty)", ModelModalities.FindUnknownToken(["\t"]));
    }

    [Fact]
    public void FindUnknownToken_NullList_ReturnsNull()
    {
        Assert.Null(ModelModalities.FindUnknownToken(null));
        Assert.Null(ModelModalities.FindUnknownToken([]));
    }

    // ── ParseModalityString ───────────────────────────────────────────

    [Fact]
    public void ParseModalityString_OpenRouterTextImage_ReturnsCanonicalInputs()
    {
        Assert.Equal(["text", "image"], ModelModalities.ParseModalityString("text+image->text"));
    }

    [Fact]
    public void ParseModalityString_UnknownTokensDroppedButTextKept()
    {
        Assert.Equal(["text", "audio"], ModelModalities.ParseModalityString("telepathy+audio->text"));
    }

    [Fact]
    public void ParseModalityString_NoArrow_ParsesWholeString()
    {
        Assert.Equal(["text", "video"], ModelModalities.ParseModalityString("text+video"));
    }

    [Fact]
    public void ParseModalityString_NullOrWhitespace_ReturnsTextOnly()
    {
        Assert.Equal(["text"], ModelModalities.ParseModalityString(null));
        Assert.Equal(["text"], ModelModalities.ParseModalityString("  "));
    }

    // ── ParseJson ─────────────────────────────────────────────────────

    [Fact]
    public void ParseJson_NullOrEmpty_ReturnsTextOnly()
    {
        Assert.Equal(["text"], ModelModalities.ParseJson(null));
        Assert.Equal(["text"], ModelModalities.ParseJson(""));
        Assert.Equal(["text"], ModelModalities.ParseJson("   "));
    }

    [Fact]
    public void ParseJson_JsonNullLiteral_ReturnsTextOnly()
    {
        Assert.Equal(["text"], ModelModalities.ParseJson("null"));
    }

    [Fact]
    public void ParseJson_NonArray_ReturnsTextOnly()
    {
        Assert.Equal(["text"], ModelModalities.ParseJson("""{"a":1}"""));
        Assert.Equal(["text"], ModelModalities.ParseJson("\"text\""));
    }

    [Fact]
    public void ParseJson_NonStringArrayElements_ReturnsTextOnly()
    {
        Assert.Equal(["text"], ModelModalities.ParseJson("""["text",123,true]"""));
        Assert.Equal(["text"], ModelModalities.ParseJson("""[1,2,3]"""));
    }

    [Fact]
    public void ParseJson_CanonicalizesOrderDropsUnknownAndDedupes()
    {
        Assert.Equal(["text", "image", "pdf"],
            ModelModalities.ParseJson("""["pdf","image","text","image","bogus"]"""));
    }
}
