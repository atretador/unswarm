using System.Text.Json.Serialization;

namespace Unswarm.Core.Models;

/// <summary>
/// Per-key model access restrictions. <see cref="Providers"/> lists cloud
/// provider names (and, for local models, the serving runtime's display name);
/// <see cref="Models"/> lists exact model ids ("cloud/&lt;provider&gt;/&lt;model&gt;"
/// or a local model name). Both empty = unrestricted.
/// </summary>
public sealed class KeyAccess
{
    public IReadOnlyList<string> Providers { get; init; } = [];
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>
    /// Internal marker: the stored AccessJson could not be parsed (or deserialized
    /// to null). Excluded from serialization so it never leaks into the wire shape
    /// or the persisted JSON. When true, access checks must DENY everything —
    /// empty allow-lists alone mean unrestricted, so the marker is what keeps a
    /// corrupt record from failing open.
    /// </summary>
    [JsonIgnore]
    public bool IsMalformed { get; init; }

    /// <summary>Sentinel for unparsable AccessJson — denies every model.</summary>
    public static readonly KeyAccess Denied = new() { IsMalformed = true };
}
