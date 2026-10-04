namespace KRWardrobeCleaner;

public sealed record Candidate(uint ItemId, string Name, string Source, bool AlreadyInArmoire = false);
public sealed record TraceValue(string Type, string? Value);

public sealed record AgentTrace(
    DateTimeOffset Timestamp,
    string Agent,
    ulong EventKind,
    uint ValueCount,
    IReadOnlyList<TraceValue> Values,
    uint? ArmedItemId,
    string? ArmedItemName);

public sealed class ScanResult
{
    public string? SnapshotPath { get; init; }
    public int DresserCount { get; init; }
    public int ArmoireCount { get; init; }
    public int CabinetEligibleCount { get; init; }
    public List<Candidate> Candidates { get; init; } = [];

    // Raw Dungeon Drip ownership sets used by the general dresser cleanup stages.
    // DresserDirectItems are pieces occupying an individual dresser slot.
    // DresserOutfitPieces are pieces currently held inside a stored Outfit Glamour set.
    public HashSet<uint> DresserDirectItems { get; init; } = [];
    public HashSet<uint> DresserOutfitPieces { get; init; } = [];
    public HashSet<uint> StoredOutfitIds { get; init; } = [];

    public List<string> DresserPaths { get; init; } = [];
    public List<string> ArmoirePaths { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}
