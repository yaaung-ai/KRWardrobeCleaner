using System.Text.Json;

namespace KRWardrobeCleaner;

public sealed class DungeonDripSnapshot
{
    private readonly ExcelIndex excel;

    public DungeonDripSnapshot(ExcelIndex excel) => this.excel = excel;

    public string? FindNewestSnapshot(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            Path.Combine(appData, "XIVLauncherKR", "pluginConfigs"),
            Path.Combine(appData, "XIVLauncher", "pluginConfigs"),
        };

        return roots.Where(Directory.Exists)
            .SelectMany(root => SafeEnumerate(root, "ownership-*.json"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public ScanResult Scan(string? configuredPath)
    {
        var path = FindNewestSnapshot(configuredPath);
        if (path is null)
            return new ScanResult { Notes = ["Dungeon Drip 소유권 스냅샷(ownership-*.json)을 찾지 못했습니다."] };

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var dresserDirect = ReadUIntArray(root, "DresserDirect");
        var armoire = ReadUIntArray(root, "Armoire");
        var slotsUsed = ReadInt(root, "DresserSlotsUsed");

        var cabinet = excel.CabinetItems;
        var candidates = dresserDirect
            .Where(cabinet.Contains)
            .Where(id => !armoire.Contains(id))
            .OrderBy(id => excel.NameOf(id), StringComparer.CurrentCulture)
            .Select(id => new Candidate(id, excel.NameOf(id), "Dungeon Drip 환상의 옷장 + 장롱 가능 목록"))
            .ToList();

        var notes = new List<string>();
        if (dresserDirect.Count == 0)
            notes.Add("환상의 옷장 아이템을 찾지 못했습니다. 환상의 옷장을 연 뒤 /dungeondrip refresh를 실행하고 다시 검색해 주세요.");
        if (dresserDirect.Count > 0 && candidates.Count == 0)
            notes.Add("환상의 옷장 데이터는 있지만 장롱 보관 후보가 없습니다. 장롱을 한 번 연 뒤 /dungeondrip refresh를 실행하고 다시 검색해 주세요.");

        return new ScanResult
        {
            SnapshotPath = path,
            DresserCount = slotsUsed >= 0 ? slotsUsed : dresserDirect.Count,
            ArmoireCount = armoire.Count,
            CabinetEligibleCount = cabinet.Count,
            Candidates = candidates,
            DresserPaths = root.TryGetProperty("DresserDirect", out _) ? ["$.DresserDirect"] : [],
            ArmoirePaths = root.TryGetProperty("Armoire", out _) ? ["$.Armoire"] : [],
            Notes = notes,
        };
    }

    private static HashSet<uint> ReadUIntArray(JsonElement root, string propertyName)
    {
        var result = new HashSet<uint>();
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var value in element.EnumerateArray())
            if (value.TryGetUInt32(out var id) && id != 0)
                result.Add(id);

        return result;
    }

    private static int ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || !element.TryGetInt32(out var value))
            return -1;
        return value;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray(); }
        catch { return []; }
    }
}
