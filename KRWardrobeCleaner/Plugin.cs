using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IAgentLifecycle AgentLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly Configuration config;
    private readonly DungeonDripSnapshot snapshots;
    private readonly CalibrationRecorder recorder;
    private readonly DiagnosticExporter exporter;

    private ScanResult scan = new();
    private bool windowOpen = true;
    private int selectedIndex = -1;
    private string status = "Ready. Open the Glamour Dresser and Armoire once, then press Rescan.";

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var excel = new ExcelIndex(Data);
        snapshots = new DungeonDripSnapshot(excel);
        recorder = new CalibrationRecorder(AgentLifecycle, Log);
        recorder.SetEnabled(config.CalibrationMode);
        exporter = new DiagnosticExporter(PluginInterface);

        Commands.AddHandler("/kwc", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open KR Wardrobe Cleaner. /kwc scan, /kwc calibrate, /kwc armoire",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;

        Rescan();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/kwc");
        recorder.Dispose();
    }

    private void Open() => windowOpen = true;

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "scan":
                Rescan();
                windowOpen = true;
                break;
            case "calibrate":
                config.CalibrationMode = !config.CalibrationMode;
                recorder.SetEnabled(config.CalibrationMode);
                SaveConfig();
                status = $"Calibration logging: {(config.CalibrationMode ? "ON" : "OFF")}";
                windowOpen = true;
                break;
            case "armoire":
                RunAutoRetainerArmoire();
                break;
            default:
                windowOpen = !windowOpen;
                break;
        }
    }

    private void Rescan()
    {
        try
        {
            status = "Scanning Dungeon Drip snapshot and Cabinet sheet...";
            scan = snapshots.Scan(config.OwnershipSnapshotPath);
            if (scan.SnapshotPath is not null)
                config.OwnershipSnapshotPath = scan.SnapshotPath;

            selectedIndex = scan.Candidates.Count > 0 ? 0 : -1;
            SaveConfig();
            status = $"Scan complete: {scan.Candidates.Count} Armoire candidates.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[KWC] Scan failed");
            status = $"Scan failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void RunAutoRetainerArmoire()
    {
        if (!Commands.Commands.ContainsKey("/autoretainer"))
        {
            status = "AutoRetainer command was not found. Restore items to bags first, then install/enable AutoRetainer or deposit manually.";
            return;
        }

        var ok = Commands.ProcessCommand("/autoretainer armoire");
        status = ok ? "Sent /autoretainer armoire." : "AutoRetainer did not accept the command.";
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(720, 640), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("KR Wardrobe Cleaner v0.1###KRWardrobeCleaner", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Phase 1: candidate scan + KR event calibration");
        ImGui.TextWrapped("This build does not batch-restore dresser items yet. It identifies Armoire candidates from Dungeon Drip and records the KR-client restore event.");
        ImGui.Separator();

        if (ImGui.Button("Rescan Dungeon Drip")) Rescan();
        ImGui.SameLine();
        if (ImGui.Button("Refresh Dungeon Drip")) Commands.ProcessCommand("/dungeondrip refresh");
        ImGui.SameLine();
        if (ImGui.Button("Deposit carried -> Armoire")) RunAutoRetainerArmoire();

        ImGui.TextWrapped($"Status: {status}");
        ImGui.TextWrapped($"Snapshot: {scan.SnapshotPath ?? "not found"}");
        ImGui.TextUnformatted($"Dresser IDs: {scan.DresserCount} | Armoire IDs: {scan.ArmoireCount} | Cabinet rows: {scan.CabinetEligibleCount} | Candidates: {scan.Candidates.Count}");

        foreach (var note in scan.Notes)
            ImGui.TextWrapped("Note: " + note);

        var cal = config.CalibrationMode;
        if (ImGui.Checkbox("Calibration logging", ref cal))
        {
            config.CalibrationMode = cal;
            recorder.SetEnabled(cal);
            SaveConfig();
        }

        if (selectedIndex >= 0 && selectedIndex < scan.Candidates.Count)
        {
            var candidate = scan.Candidates[selectedIndex];
            if (ImGui.Button($"Arm calibration for selected: {candidate.Name}"))
            {
                recorder.Arm(candidate);
                config.CalibrationMode = true;
                recorder.SetEnabled(true);
                SaveConfig();
                status = $"Armed for {candidate.Name}. Restore this item manually, confirm dialogs, then export diagnostics.";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Export diagnostics"))
        {
            try
            {
                var path = exporter.Export(scan, recorder.Traces);
                status = "Diagnostic saved: " + path;
                Chat.Print("[KWC] Diagnostic saved: " + path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[KWC] Diagnostic export failed");
                status = "Export failed: " + ex.Message;
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Armoire candidates");
        ImGui.BeginChild("Candidates", new Vector2(0, 330), true);

        for (var i = 0; i < scan.Candidates.Count; i++)
        {
            var candidate = scan.Candidates[i];
            var label = config.ShowIds ? $"{candidate.Name} [{candidate.ItemId}]" : candidate.Name;
            if (ImGui.Selectable(label + $"##{candidate.ItemId}", selectedIndex == i))
                selectedIndex = i;
        }

        ImGui.EndChild();

        var showIds = config.ShowIds;
        if (ImGui.Checkbox("Show item IDs", ref showIds))
        {
            config.ShowIds = showIds;
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"Captured agent events: {recorder.Traces.Count}");

        if (ImGui.CollapsingHeader("Snapshot parser paths"))
        {
            ImGui.TextUnformatted("Dresser-like paths:");
            foreach (var p in scan.DresserPaths) ImGui.BulletText(p);
            ImGui.TextUnformatted("Armoire-like paths:");
            foreach (var p in scan.ArmoirePaths) ImGui.BulletText(p);
        }

        ImGui.End();
    }
}
