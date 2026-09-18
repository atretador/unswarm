using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for <see cref="CloudProviderModelsJsonHelper"/> — parsing, serialization,
/// and backward compatibility between legacy bare-string and current object formats.
/// </summary>
public sealed class CloudProviderModelsJsonHelperTests
{
    // ── Parse: legacy bare-string format ───────────────────────────────

    [Fact]
    public void Parse_LegacyBareStrings_ReturnsMetasWithIds()
    {
        var json = """["gpt-4o","gpt-4o-mini","o1-preview"]""";

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Equal(3, result.Count);
        Assert.Equal("gpt-4o", result[0].Id);
        Assert.Equal("gpt-4o-mini", result[1].Id);
        Assert.Equal("o1-preview", result[2].Id);
        // All metadata should be default (zero/empty)
        Assert.Equal(0, result[0].ContextWindow);
        Assert.Equal("", result[0].Family);
    }

    [Fact]
    public void Parse_LegacyBareStrings_PreservesOrder()
    {
        var json = """["z-model","a-model","m-model"]""";

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Equal("z-model", result[0].Id);
        Assert.Equal("a-model", result[1].Id);
        Assert.Equal("m-model", result[2].Id);
    }

    // ── Parse: current object format ───────────────────────────────────

    [Fact]
    public void Parse_ObjectFormat_ExtractsAllFields()
    {
        var json = """
        [
          {
            "id": "gpt-5.4",
            "contextWindow": 1050000,
            "maxOutputTokens": 128000,
            "family": "gpt",
            "parameterSize": "",
            "quantization": "",
            "displayName": "GPT 5.4"
          }
        ]
        """;

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Single(result);
        Assert.Equal("gpt-5.4", result[0].Id);
        Assert.Equal(1050000, result[0].ContextWindow);
        Assert.Equal(128000, result[0].MaxOutputTokens);
        Assert.Equal("gpt", result[0].Family);
        Assert.Equal("GPT 5.4", result[0].DisplayName);
    }

    [Fact]
    public void Parse_ObjectFormat_MissingFields_DefaultsToZeroEmpty()
    {
        var json = """[{"id": "model-a"}]""";

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Single(result);
        Assert.Equal("model-a", result[0].Id);
        Assert.Equal(0, result[0].ContextWindow);
        Assert.Equal(0, result[0].MaxOutputTokens);
        Assert.Equal("", result[0].Family);
        Assert.Equal("", result[0].ParameterSize);
        Assert.Equal("", result[0].Quantization);
        Assert.Equal("", result[0].DisplayName);
    }

    [Fact]
    public void Parse_ObjectFormat_SkipsEntriesWithEmptyId()
    {
        var json = """
        [
          {"id": "valid-model", "contextWindow": 200000},
          {"id": "", "contextWindow": 100000},
          {"contextWindow": 50000}
        ]
        """;

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Single(result);
        Assert.Equal("valid-model", result[0].Id);
    }

    [Fact]
    public void Parse_ObjectFormat_MultipleEntries()
    {
        var json = """
        [
          {"id": "claude-sonnet-4-5", "contextWindow": 200000, "family": "claude", "displayName": "Claude Sonnet 4.5"},
          {"id": "gpt-5.4", "contextWindow": 1050000, "family": "gpt"},
          {"id": "deepseek-v4-pro", "contextWindow": 131072, "maxOutputTokens": 65536}
        ]
        """;

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Equal(3, result.Count);
        Assert.Equal("claude-sonnet-4-5", result[0].Id);
        Assert.Equal(200000, result[0].ContextWindow);
        Assert.Equal("claude", result[0].Family);
        Assert.Equal("gpt-5.4", result[1].Id);
        Assert.Equal(1050000, result[1].ContextWindow);
        Assert.Equal("deepseek-v4-pro", result[2].Id);
        Assert.Equal(65536, result[2].MaxOutputTokens);
    }

    // ── Parse: edge cases ─────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(CloudProviderModelsJsonHelper.Parse("[]"));
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(CloudProviderModelsJsonHelper.Parse(""));
        Assert.Empty(CloudProviderModelsJsonHelper.Parse("  "));
        Assert.Empty(CloudProviderModelsJsonHelper.Parse(null!));
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmpty()
    {
        Assert.Empty(CloudProviderModelsJsonHelper.Parse("{not json"));
        Assert.Empty(CloudProviderModelsJsonHelper.Parse("[]]"));
    }

    [Fact]
    public void Parse_JsonObjectNotArray_ReturnsEmpty()
    {
        Assert.Empty(CloudProviderModelsJsonHelper.Parse("""{"id": "model"}"""));
    }

    // ── ParseIds ──────────────────────────────────────────────────────

    [Fact]
    public void ParseIds_LegacyFormat_ReturnsIds()
    {
        var json = """["gpt-4o","claude-3"]""";
        var ids = CloudProviderModelsJsonHelper.ParseIds(json);

        Assert.Equal(2, ids.Count);
        Assert.Equal("gpt-4o", ids[0]);
        Assert.Equal("claude-3", ids[1]);
    }

    [Fact]
    public void ParseIds_ObjectFormat_ReturnsIds()
    {
        var json = """[{"id":"gpt-5.4","contextWindow":100000},{"id":"claude-4","contextWindow":200000}]""";
        var ids = CloudProviderModelsJsonHelper.ParseIds(json);

        Assert.Equal(2, ids.Count);
        Assert.Contains("gpt-5.4", ids);
        Assert.Contains("claude-4", ids);
    }

    [Fact]
    public void ParseIds_EmptyJson_ReturnsEmpty()
    {
        Assert.Empty(CloudProviderModelsJsonHelper.ParseIds("[]"));
    }

    // ── Serialize ─────────────────────────────────────────────────────

    [Fact]
    public void Serialize_ProducesCamelCaseJson()
    {
        var metas = new List<CloudProviderModelMeta>
        {
            CloudProviderModelMeta.FromId("gpt-4o")
        };

        var json = CloudProviderModelsJsonHelper.Serialize(metas);

        Assert.Contains("\"id\"", json);
        Assert.Contains("\"gpt-4o\"", json);
        // Should NOT contain PascalCase property names
        Assert.DoesNotContain("\"Id\"", json);
        Assert.DoesNotContain("\"ContextWindow\"", json);
    }

    [Fact]
    public void Serialize_IncludesAllFields()
    {
        var metas = new List<CloudProviderModelMeta>
        {
            new()
            {
                Id = "claude-sonnet-4-5",
                ContextWindow = 200000,
                MaxOutputTokens = 64000,
                Family = "claude",
                ParameterSize = "",
                Quantization = "",
                DisplayName = "Claude Sonnet 4.5"
            }
        };

        var json = CloudProviderModelsJsonHelper.Serialize(metas);

        Assert.Contains("\"contextWindow\":200000", json);
        Assert.Contains("\"maxOutputTokens\":64000", json);
        Assert.Contains("\"family\":\"claude\"", json);
        Assert.Contains("\"displayName\":\"Claude Sonnet 4.5\"", json);
    }

    [Fact]
    public void Serialize_EmptyList_ProducesEmptyArray()
    {
        var json = CloudProviderModelsJsonHelper.Serialize([]);
        Assert.Equal("[]", json);
    }

    // ── Roundtrip ─────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_SerializeThenParse_PreservesAllData()
    {
        var original = new List<CloudProviderModelMeta>
        {
            new()
            {
                Id = "gpt-5.4",
                ContextWindow = 1050000,
                MaxOutputTokens = 128000,
                Family = "gpt",
                ParameterSize = "",
                Quantization = "",
                DisplayName = "GPT 5.4"
            },
            new()
            {
                Id = "claude-sonnet-4-5",
                ContextWindow = 200000,
                MaxOutputTokens = 64000,
                Family = "claude",
                DisplayName = "Claude Sonnet 4.5"
            }
        };

        var json = CloudProviderModelsJsonHelper.Serialize(original);
        var parsed = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Equal(original.Count, parsed.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Id, parsed[i].Id);
            Assert.Equal(original[i].ContextWindow, parsed[i].ContextWindow);
            Assert.Equal(original[i].MaxOutputTokens, parsed[i].MaxOutputTokens);
            Assert.Equal(original[i].Family, parsed[i].Family);
            Assert.Equal(original[i].DisplayName, parsed[i].DisplayName);
        }
    }

    [Fact]
    public void Roundtrip_ParseIds_ThenSerialize_PreservesIds()
    {
        var originalIds = new[] { "gpt-4o", "claude-3", "deepseek-v4" };
        var metas = originalIds.Select(CloudProviderModelMeta.FromId).ToList();

        var json = CloudProviderModelsJsonHelper.Serialize(metas);
        var parsedIds = CloudProviderModelsJsonHelper.ParseIds(json);

        Assert.Equal(originalIds.Length, parsedIds.Count);
        Assert.Equal(originalIds, parsedIds);
    }

    // ── Backward compat: mixed formats ────────────────────────────────

    [Fact]
    public void Parse_MixedFormats_HandlesGracefully()
    {
        // Simulates a scenario where some entries are old format and some are new
        // This shouldn't happen in practice, but Parse should handle it
        var json = """
        [
          "legacy-model",
          {"id": "new-model", "contextWindow": 100000}
        ]
        """;

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("legacy-model", result[0].Id);
        Assert.Equal(0, result[0].ContextWindow); // legacy has no metadata
        Assert.Equal("new-model", result[1].Id);
        Assert.Equal(100000, result[1].ContextWindow);
    }

    // ── Input modalities ──────────────────────────────────────────────

    [Fact]
    public void Parse_ObjectFormat_InputModalities_NormalizesToCanonicalOrder()
    {
        var json = """[{"id":"m","inputModalities":["pdf","bogus","image"]}]""";

        var result = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Single(result);
        Assert.Equal(["text", "image", "pdf"], result[0].InputModalities);
    }

    [Fact]
    public void Parse_LegacyBareString_InputModalities_DefaultsToTextOnly()
    {
        var result = CloudProviderModelsJsonHelper.Parse("""["legacy-model"]""");

        Assert.Single(result);
        Assert.Equal(["text"], result[0].InputModalities);
    }

    [Fact]
    public void Parse_ObjectWithoutInputModalities_DefaultsToTextOnly()
    {
        var result = CloudProviderModelsJsonHelper.Parse("""[{"id":"old-object","contextWindow":128000}]""");

        Assert.Single(result);
        Assert.Equal(["text"], result[0].InputModalities);
    }

    [Fact]
    public void Roundtrip_InputModalities_PreservesValues()
    {
        var original = new List<CloudProviderModelMeta>
        {
            new() { Id = "vision", InputModalities = ["text", "image", "video"] },
            new() { Id = "text-only", InputModalities = ["text"] },
        };

        var json = CloudProviderModelsJsonHelper.Serialize(original);
        var parsed = CloudProviderModelsJsonHelper.Parse(json);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(original[0].InputModalities, parsed[0].InputModalities);
        Assert.Equal(original[1].InputModalities, parsed[1].InputModalities);
    }
}
