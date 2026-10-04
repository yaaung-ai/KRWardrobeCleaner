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
            return new ScanResult { Notes = ["Dungeon Drip ownership-*.json snapshot was not found."] };

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dresser = new HashSet<uint>();
        var armoire = new HashSet<uint>();
        var dresserPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var armoirePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(doc.RootElement, "$", false, false, dresser, armoire, dresserPaths, armoirePaths);

        var cabinet = excel.CabinetItems;
        var candidates = dresser
            .Where(cabinet.Contains)
            .Where(id => !armoire.Contains(id))
            .OrderBy(id => excel.NameOf(id), StringComparer.CurrentCulture)
            .Select(id => new Candidate(id, excel.NameOf(id), "DungeonDrip snapshot + Cabinet sheet"))
            .ToList();

        var notes = new List<string>();
        if (dresser.Count == 0)
            notes.Add("No dresser item IDs were discovered. Send the ownership-*.json file so the parser can be matched to this Dungeon Drip version.");
        if (dresser.Count > 0 && candidates.Count == 0)
            notes.Add("Dresser data was found, but no Armoire candidate was produced. Open the Armoire once, run /dungeondrip refresh, then rescan.");

        return new ScanResult
        {
            SnapshotPath = path,
            DresserCount = dresser.Count,
            ArmoireCount = armoire.Count,
            CabinetEligibleCount = cabinet.Count,
            Candidates = candidates,
            DresserPaths = dresserPaths.Order().ToList(),
            ArmoirePaths = armoirePaths.Order().ToList(),
            Notes = notes,
        };
    }

    private void Walk(
        JsonElement e,
        string path,
        bool inDresser,
        bool inArmoire,
        HashSet<uint> dresser,
        HashSet<uint> armoire,
        HashSet<string> dresserPaths,
        HashSet<string> armoirePaths)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                {
                    var n = prop.Name;
                    var d = inDresser || ContainsAny(n, "dresser", "prismbox", "prism_box", "mirageprism");
                    var a = inArmoire || ContainsAny(n, "armoire", "cabinet");
                    var p = path + "." + n;
                    if (!inDresser && d) dresserPaths.Add(p);
                    if (!inArmoire && a) armoirePaths.Add(p);
                    Walk(prop.Value, p, d, a, dresser, armoire, dresserPaths, armoirePaths);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var child in e.EnumerateArray())
                    Walk(child, $"{path}[{i++}]", inDresser, inArmoire, dresser, armoire, dresserPaths, armoirePaths);
                break;

            case JsonValueKind.Number:
                if (!e.TryGetUInt32(out var id) || id == 0 || !excel.ValidItems.Contains(id)) return;
                if (inDresser) dresser.Add(id);
                if (inArmoire) armoire.Add(id);
                break;
        }
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray(); }
        catch { return []; }
    }
}
