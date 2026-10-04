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
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private readonly Configuration config;
    private readonly DungeonDripSnapshot snapshots;
    private readonly CalibrationRecorder recorder;
    private readonly DiagnosticExporter exporter;
    private readonly DresserRestoreTester restoreTester;
    private readonly WardrobeCleanupService cleanup;

    private ScanResult scan = new();
    private bool windowOpen = true;
    private int selectedIndex = -1;
    private string status = "환상의 옷장과 장롱을 한 번씩 연 뒤 후보를 검색해 주세요.";

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var excel = new ExcelIndex(Data);
        snapshots = new DungeonDripSnapshot(excel);
        recorder = new CalibrationRecorder(AgentLifecycle, Log);
        recorder.SetEnabled(config.CalibrationMode);
        exporter = new DiagnosticExporter(PluginInterface);
        restoreTester = new DresserRestoreTester(GameGui);
        cleanup = new WardrobeCleanupService(restoreTester, Commands, config, SaveConfig);

        Commands.AddHandler("/kwc", new CommandInfo(OnCommand)
        {
            HelpMessage = "한국판 옷장 정리기를 엽니다. /kwc scan(재검색), /kwc stop(중지), /kwc armoire(장롱 보관)",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += OnFrameworkUpdate;

        Rescan();
    }

    public void Dispose()
    {
        cleanup.Stop("플러그인이 종료되어 정리를 중지했습니다.");
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/kwc");
        recorder.Dispose();
    }

    private void Open() => windowOpen = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        cleanup.Tick();

        if (cleanup.ConsumeRescanRequest())
        {
            status = cleanup.Status;
            Rescan(updateStatus: false);
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "scan":
            case "검색":
                Rescan();
                windowOpen = true;
                break;
            case "stop":
            case "중지":
                cleanup.Stop();
                status = cleanup.Status;
                windowOpen = true;
                break;
            case "armoire":
            case "장롱":
                cleanup.DepositNow();
                status = cleanup.Status;
                windowOpen = true;
                break;
            case "calibrate":
            case "진단":
                config.CalibrationMode = !config.CalibrationMode;
                recorder.SetEnabled(config.CalibrationMode);
                SaveConfig();
                status = $"진단 이벤트 기록: {(config.CalibrationMode ? "켜짐" : "꺼짐")}";
                windowOpen = true;
                break;
            default:
                windowOpen = !windowOpen;
                break;
        }
    }

    private void Rescan(bool updateStatus = true)
    {
        try
        {
            if (updateStatus)
                status = "Dungeon Drip 스냅샷과 장롱 가능 목록을 확인하는 중입니다...";

            scan = snapshots.Scan(config.OwnershipSnapshotPath);
            if (scan.SnapshotPath is not null)
                config.OwnershipSnapshotPath = scan.SnapshotPath;

            selectedIndex = scan.Candidates.Count > 0 ? Math.Clamp(selectedIndex, 0, scan.Candidates.Count - 1) : -1;
            SaveConfig();

            if (updateStatus)
                status = $"검색 완료: 장롱으로 옮길 수 있는 후보 {scan.Candidates.Count}개.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[KWC] 스캔 실패");
            status = $"검색 실패: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(780, 720), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("한국판 옷장 정리기 v0.3###KRWardrobeCleaner", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("환상의 옷장 → 장롱 정리");
        ImGui.TextWrapped("장롱에 보관할 수 있는 환상의 옷장 아이템을 찾아 순서대로 복원합니다. 염색된 아이템과 중복 대상은 안전을 위해 자동으로 제외합니다.");
        ImGui.Separator();

        if (ImGui.Button("후보 다시 검색"))
            Rescan();

        ImGui.SameLine();
        if (ImGui.Button("Dungeon Drip 새로고침"))
        {
            Commands.ProcessCommand("/dungeondrip refresh");
            status = "Dungeon Drip 새로고침을 요청했습니다.";
        }

        ImGui.SameLine();
        if (ImGui.Button("장롱 보관 실행"))
        {
            cleanup.DepositNow();
            status = cleanup.Status;
        }

        var displayedStatus = cleanup.IsRunning || cleanup.IsWaitingForAutoDeposit ? cleanup.Status : status;
        ImGui.TextWrapped($"상태: {displayedStatus}");
        if (!string.IsNullOrWhiteSpace(cleanup.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {cleanup.LastItemStatus}");

        var freeSlots = restoreTester.GetFreeBagSlots();
        ImGui.TextUnformatted(
            $"환상의 옷장 사용: {scan.DresserCount} | 장롱 기록: {scan.ArmoireCount} | 정리 후보: {scan.Candidates.Count} | 가방 빈칸: {(freeSlots < 0 ? "확인 불가" : freeSlots)}");

        foreach (var note in scan.Notes)
            ImGui.TextWrapped("안내: " + note);

        ImGui.Separator();
        ImGui.TextUnformatted("자동 정리");

        if (!cleanup.IsRunning)
        {
            if (ImGui.Button($"안전 후보 전체 정리 시작 ({scan.Candidates.Count}개)"))
            {
                if (cleanup.Start(scan.Candidates))
                    status = cleanup.Status;
                else
                    status = cleanup.Status;
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: {cleanup.Processed}/{cleanup.Total} · 복원 {cleanup.Restored} · 제외 {cleanup.Skipped} · 실패 {cleanup.Failed}");
            if (ImGui.Button("정리 중지"))
            {
                cleanup.Stop();
                status = cleanup.Status;
            }
        }

        ImGui.SameLine();
        if (selectedIndex >= 0 && selectedIndex < scan.Candidates.Count && !cleanup.IsRunning)
        {
            var selected = scan.Candidates[selectedIndex];
            if (ImGui.Button($"선택 항목 1개만 복원##one{selected.ItemId}"))
            {
                var result = restoreTester.RestoreOne(selected);
                status = result.Message;
                if (result.Success)
                {
                    Chat.Print("[옷장 정리기] " + result.Message);
                    Commands.ProcessCommand("/dungeondrip refresh");
                }
                else
                {
                    Chat.PrintError("[옷장 정리기] " + result.Message);
                }
            }
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("정리 옵션", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var interval = config.RestoreIntervalMs;
            if (ImGui.SliderInt("아이템 복원 간격 (ms)", ref interval, 300, 3000))
                cleanup.UpdateSettings(interval, config.ReserveFreeSlots, config.AutoDepositToArmoire);

            var reserve = config.ReserveFreeSlots;
            if (ImGui.SliderInt("남겨둘 가방 빈칸", ref reserve, 0, 30))
                cleanup.UpdateSettings(config.RestoreIntervalMs, reserve, config.AutoDepositToArmoire);

            var autoDeposit = config.AutoDepositToArmoire;
            if (ImGui.Checkbox("복원 완료 후 AutoRetainer로 장롱에 자동 보관", ref autoDeposit))
                cleanup.UpdateSettings(config.RestoreIntervalMs, config.ReserveFreeSlots, autoDeposit);

            ImGui.TextWrapped("자동 장롱 보관을 켜면 복원이 끝난 뒤 환상의 옷장을 닫았을 때 /autoretainer armoire 명령을 실행합니다.");

            var showIds = config.ShowIds;
            if (ImGui.Checkbox("목록에 아이템 ID 표시", ref showIds))
            {
                config.ShowIds = showIds;
                SaveConfig();
            }

            ImGui.TextWrapped("안전 규칙: 염색 아이템은 항상 제외하며, 동일 아이템이 여러 칸에서 발견되면 자동으로 건너뜁니다.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("장롱 보관 후보");
        ImGui.BeginChild("Candidates", new Vector2(0, 300), true);

        for (var i = 0; i < scan.Candidates.Count; i++)
        {
            var candidate = scan.Candidates[i];
            var label = config.ShowIds ? $"{candidate.Name} [{candidate.ItemId}]" : candidate.Name;
            if (ImGui.Selectable(label + $"##{candidate.ItemId}", selectedIndex == i))
                selectedIndex = i;
        }

        ImGui.EndChild();

        if (ImGui.CollapsingHeader("진단 도구"))
        {
            var cal = config.CalibrationMode;
            if (ImGui.Checkbox("게임 UI 이벤트 기록", ref cal))
            {
                config.CalibrationMode = cal;
                recorder.SetEnabled(cal);
                SaveConfig();
            }

            if (selectedIndex >= 0 && selectedIndex < scan.Candidates.Count)
            {
                var candidate = scan.Candidates[selectedIndex];
                if (ImGui.Button($"선택 항목 진단 준비: {candidate.Name}"))
                {
                    recorder.Arm(candidate);
                    config.CalibrationMode = true;
                    recorder.SetEnabled(true);
                    SaveConfig();
                    status = $"{candidate.Name} 진단 준비 완료. 이 아이템을 수동 복원한 뒤 진단 파일을 내보내세요.";
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("진단 파일 내보내기"))
            {
                try
                {
                    var path = exporter.Export(scan, recorder.Traces);
                    status = "진단 파일 저장 완료: " + path;
                    Chat.Print("[옷장 정리기] 진단 파일 저장: " + path);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[KWC] 진단 파일 저장 실패");
                    status = "진단 파일 저장 실패: " + ex.Message;
                }
            }

            ImGui.TextUnformatted($"기록된 UI 이벤트: {recorder.Traces.Count}");

            if (ImGui.CollapsingHeader("스냅샷 파서 경로"))
            {
                ImGui.TextUnformatted("환상의 옷장 경로:");
                foreach (var p in scan.DresserPaths) ImGui.BulletText(p);
                ImGui.TextUnformatted("장롱 경로:");
                foreach (var p in scan.ArmoirePaths) ImGui.BulletText(p);
            }
        }

        ImGui.End();
    }
}
