using System.Text.Json;
using Dalamud.Plugin;

namespace KRWardrobeCleaner;

public sealed class DiagnosticExporter
{
    private readonly IDalamudPluginInterface pi;

    public DiagnosticExporter(IDalamudPluginInterface pi) => this.pi = pi;

    public string Export(ScanResult scan, IReadOnlyList<AgentTrace> traces)
    {
        var path = Path.Combine(pi.ConfigDirectory.FullName, $"kwc-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(pi.ConfigDirectory.FullName);
        var payload = new
        {
            GeneratedAt = DateTimeOffset.Now,
            scan.SnapshotPath,
            scan.DresserCount,
            scan.ArmoireCount,
            scan.CabinetEligibleCount,
            Candidates = scan.Candidates,
            scan.DresserPaths,
            scan.ArmoirePaths,
            scan.Notes,
            AgentTraces = traces,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
