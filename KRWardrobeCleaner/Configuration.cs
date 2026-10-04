using Dalamud.Configuration;

namespace KRWardrobeCleaner;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string? OwnershipSnapshotPath { get; set; }
    public bool ShowIds { get; set; }
    public bool CalibrationMode { get; set; }
}
