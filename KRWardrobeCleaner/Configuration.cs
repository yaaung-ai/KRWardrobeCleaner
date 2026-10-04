using Dalamud.Configuration;

namespace KRWardrobeCleaner;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public string? OwnershipSnapshotPath { get; set; }
    public bool ShowIds { get; set; }
    public bool CalibrationMode { get; set; }
    public int RestoreIntervalMs { get; set; } = 700;
    public int ReserveFreeSlots { get; set; } = 5;
    public bool AutoDepositToArmoire { get; set; }
    public bool IncludeCraftingGearInArmoryPreclean { get; set; }
    public int ArmoryStoreIntervalMs { get; set; } = 700;
}
