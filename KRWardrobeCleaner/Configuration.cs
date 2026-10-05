using Dalamud.Configuration;

namespace KRWardrobeCleaner;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;

    public bool Stage1IncludeDyed { get; set; }
    public bool Stage1IncludePlateRegistered { get; set; }

    public bool Stage2IncludeDyed { get; set; }

    public bool Stage3SmartIncludeCrafterGatherer { get; set; }
    public int Stage3SmartMaxLevel { get; set; } = 100;
}
