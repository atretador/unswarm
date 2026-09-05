namespace Unswarm.Core.Models;

/// <summary>
/// Configures the directory where host runtime scripts (.sh) are stored.
/// Used for listing available scripts and storing uploaded scripts.
/// Default is ~/.config/unswarm/scripts/ (bare metal) or /data/scripts (Docker).
/// </summary>
public sealed class HostScriptsOptions
{
    public const string SectionName = "HostScripts";

    /// <summary>
    /// Absolute path to the directory containing runtime scripts.
    /// </summary>
    public string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "unswarm", "scripts");
}
