namespace Unswarm.Core.Models;

/// <summary>
/// Discriminator for the type of runtime a registered entry represents.
/// Serialized lowercase on the wire (e.g. "container", "script") via the
/// JsonStringEnumConverter configured in Program.cs.
/// </summary>
public enum RuntimeKind
{
    Container,
    Script
}
