using System.Text.Json;

namespace Unswarm.Core.Models;

/// <summary>
/// Canonical input-modality tokens and JSON (de)serialization helpers.
/// Model metadata stores modalities as a JSON array of lowercase tokens
/// (e.g. <c>["text","image"]</c>). Missing/empty/unknown values default to
/// text-only. Canonical order: text, image, video, audio, pdf.
/// </summary>
public static class ModelModalities
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Pdf = "pdf";

    /// <summary>Canonical display/storage order; the first token is always present.</summary>
    public static readonly IReadOnlyList<string> CanonicalOrder = [Text, Image, Video, Audio, Pdf];

    /// <summary>Fresh text-only array (callers own the array).</summary>
    public static string[] TextOnly() => [Text];

    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { Text, Image, Video, Audio, Pdf };

    /// <summary>True when the token is a recognized modality (case-insensitive).</summary>
    public static bool IsKnown(string? token)
        => !string.IsNullOrWhiteSpace(token) && Allowed.Contains(token.Trim());

    /// <summary>
    /// Returns the first token that is not in the allowed set, or null when all
    /// tokens are recognized. Used to reject bad admin input with a 400.
    /// </summary>
    public static string? FindUnknownToken(IEnumerable<string>? tokens)
    {
        if (tokens is null) return null;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
                return "(empty)";
            if (!Allowed.Contains(token.Trim()))
                return token.Trim();
        }
        return null;
    }

    /// <summary>
    /// Normalize an arbitrary token list to lowercase canonical order, dropping
    /// unknown/duplicate tokens. Always contains at least <c>text</c>.
    /// </summary>
    public static string[] Normalize(IEnumerable<string>? tokens)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tokens is not null)
        {
            foreach (var token in tokens)
            {
                if (!string.IsNullOrWhiteSpace(token) && Allowed.Contains(token.Trim()))
                    set.Add(token.Trim().ToLowerInvariant());
            }
        }
        set.Add(Text);
        return CanonicalOrder.Where(set.Contains).ToArray();
    }

    /// <summary>Parse a stored JSON array; null/empty/invalid → text-only.</summary>
    public static string[] ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return TextOnly();

        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(json);
            return Normalize(parsed);
        }
        catch
        {
            return TextOnly();
        }
    }

    /// <summary>Serialize a token list to canonical JSON (always at least text).</summary>
    public static string SerializeJson(IEnumerable<string>? tokens)
        => JsonSerializer.Serialize(Normalize(tokens));

    /// <summary>
    /// Parse an OpenRouter-style <c>architecture.modality</c> string such as
    /// <c>"text+image-&gt;text"</c>: take the part before <c>-&gt;</c>, split on
    /// <c>+</c>, then normalize. Unknown input → text-only.
    /// </summary>
    public static string[] ParseModalityString(string? modality)
    {
        if (string.IsNullOrWhiteSpace(modality))
            return TextOnly();

        var arrow = modality.IndexOf("->", StringComparison.Ordinal);
        var input = arrow >= 0 ? modality[..arrow] : modality;
        var tokens = input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Normalize(tokens);
    }
}
