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
}
